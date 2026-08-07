using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution.Audio;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>MiniMax H3 frame and clip reference-conditioning graph contracts.</summary>
[Collection("VideoStagesTests")]
public class MiniMaxReferenceConditioningContractTests
{
    /// <summary>
    /// Two references resolving to the same media at the same stage resolution share one framing
    /// node, so both keyframe slots read the same output — the reuse must not drop one of them.
    /// </summary>
    [Fact]
    public async Task Refiner_first_and_last_frame_references_share_one_framing()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(MakeRef("Refiner"), MakeRef("Refiner", fromEnd: true));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(clip),
            post =>
            {
                // Core's refiner step is gated on BOTH of these being present.
                post["refinermethod"] = "PostApply";
                post["refinercontrolpercentage"] = 0.2;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        Assert.NotNull(keyframes.FirstFrame.Connection);
        Assert.NotNull(keyframes.LastFrame.Connection);
        Assert.Same(keyframes.FirstFrame.Connection, keyframes.LastFrame.Connection);
        AssertImageSource(keyframes.FirstFrame.Connection.Node, "first_frame");
        Assert.True(ReachesUpstream(
            bridge,
            keyframes.FirstFrame.Connection.Node,
            fixture.RefinerSampler(bridge).Id));

        live.AssertLive(keyframes);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task Clip_references_build_the_reference_conditioning_the_sampler_reads()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(UploadedReference("RklSU1Q=", fromEnd: false));
        clip["references"] = new JArray(
            MakeClipReference("image", "data:image/png;base64,U1VCSkVDVA==", "subject.png"),
            MakeClipReference(
                "video",
                "data:video/mp4;base64,TU9USU9O",
                "motion.mp4",
                includeSoundtrack: true),
            MakeClipReference("audio", "data:audio/wav;base64,Vk9JQ0U=", "voice.wav"));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        Assert.Equal("match", inputs["ref_image_size"]?.ToString());
        Assert.Equal(
            MiniMaxWorkflowFixture.GeneratedFrames,
            inputs["length"].Value<int>());
        Assert.All(
            new[]
            {
                "ref_images.ref_image_0",
                "ref_videos.ref_video_0",
                "ref_video_audios.ref_video_audio_0",
                "ref_audios.ref_audio_0",
            },
            key => Assert.NotNull(inputs[key]));
        // The soundtrack rides the same video load, not a second decode of the file.
        Assert.Equal(
            inputs["ref_videos.ref_video_0"][0],
            inputs["ref_video_audios.ref_video_audio_0"][0]);

        // The reference node feeds the keyframe wrapper, which feeds the sampler's positive.
        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        // A plain text encode here would mean the references never reached the tokenizer.
        Assert.Same(referenceNode, keyframes.ConditioningInput.Connection?.Node);
        Assert.Same(keyframes, StageSampler(bridge, 0).Positive.Connection?.Node);

        live.AssertLive(keyframes);
        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData("image", MediaSource.ControlNetOne, 0)]
    [InlineData("video", MediaSource.ControlNetTwo, 1)]
    [InlineData("audio", MediaSource.ControlNetThree, 2)]
    public async Task ControlNet_clip_reference_sources_reach_the_matching_H3_input(
        string kind,
        string source,
        int controlNetIndex)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["references"] = new JArray(
            MakeClipReference(kind, source: source, includeSoundtrack: kind == "video"));

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraSteps: [SeedControlNetReferenceSource(controlNetIndex)]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        GetVideoComponentsNode captured = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        if (kind == "image")
        {
            ImageFromBatchNode image = bridge.NodeAt<ImageFromBatchNode>(
                (JArray)inputs["ref_images.ref_image_0"]);
            Assert.Same(captured.Images, image.Image.Connection);
        }
        else if (kind == "video")
        {
            Assert.Same(
                captured.Images,
                bridge.ResolvePath((JArray)inputs["ref_videos.ref_video_0"]));
            Assert.Same(
                captured.Audio,
                bridge.ResolvePath((JArray)inputs["ref_video_audios.ref_video_audio_0"]));
        }
        else
        {
            Assert.Same(
                captured.Audio,
                bridge.ResolvePath((JArray)inputs["ref_audios.ref_audio_0"]));
        }

        Assert.Same(referenceNode, StageSampler(bridge, 0).Positive.Connection?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task AceStepFun_audio_clip_reference_reaches_the_H3_audio_input()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["references"] = new JArray(MakeClipReference("audio", source: "audio0"));

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraSteps: [PublishAceStepFunAudioTrackStep(0)]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        Assert.Equal(
            AudioHandler.MakeAceStepFunDecodeId(0),
            ((JArray)inputs["ref_audios.ref_audio_0"])[0].ToString());
        Assert.Same(referenceNode, StageSampler(bridge, 0).Positive.Connection?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task Continue_reserves_video_and_audio_reference_slot_zero()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject first = MakeClip(fixture.Stage());
        first["duration"] = 1.0;
        first["boundaryOut"] = Constants.BoundaryOutContinue;
        first["boundaryOutOverlap"] = 22;
        JObject second = MakeClip(fixture.Stage());
        second["duration"] = 1.0;
        second["references"] = new JArray(
            MakeClipReference(
                "video",
                "data:video/mp4;base64,TU9USU9O",
                "motion.mp4",
                includeSoundtrack: true));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(first, second),
            post => post["prompt"] = "continue this shot exactly");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        Assert.Equal("continue this shot exactly", inputs.Value<string>("prompt"));
        Assert.All(
            new[]
            {
                "ref_videos.ref_video_0",
                "ref_video_audios.ref_video_audio_0",
                "ref_videos.ref_video_1",
                "ref_video_audios.ref_video_audio_1",
            },
            key => Assert.NotNull(inputs[key]));

        ImageFromBatchNode tail = bridge.NodeAt<ImageFromBatchNode>(
            (JArray)inputs["ref_videos.ref_video_0"]);
        Assert.NotNull(tail);
        Assert.Equal(17, tail.BatchIndex.LiteralAsInt());
        Assert.Equal(22, tail.Length.LiteralAsInt());
        TrimAudioDurationNode audio = bridge.NodeAt<TrimAudioDurationNode>(
            (JArray)inputs["ref_video_audios.ref_video_audio_0"]);
        Assert.NotNull(audio);
        Assert.Equal(17 / 24.0, audio.StartIndex.LiteralAsDouble().Value, 6);
        Assert.Equal(22 / 24.0, audio.Duration.LiteralAsDouble().Value, 6);

        SwarmKSamplerNode firstSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode secondSampler = StageSampler(bridge, 1);
        Assert.True(ReachesUpstream(bridge, tail, firstSampler.Id));
        Assert.True(ReachesUpstream(bridge, audio, firstSampler.Id));
        Assert.True(ReachesUpstream(bridge, secondSampler, referenceNode.Id));
        Assert.Empty(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.All(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>(),
            latent => Assert.Equal(39, latent.Length.LiteralAsInt()));

        live.AssertAllLive(referenceNode, tail, audio, firstSampler, secondSampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Continue_reference_can_scale_video_and_omit_its_soundtrack()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject first = MakeClip(fixture.Stage());
        first["duration"] = 1.0;
        first["boundaryOut"] = Constants.BoundaryOutContinue;
        first["boundaryOutOverlap"] = 22;
        first["boundaryOutReferenceScale"] = 0.25;
        first["boundaryOutReferenceIncludeSoundtrack"] = false;
        JObject second = MakeClip(fixture.Stage());
        second["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        ImageScaleByNode scaled = bridge.NodeAt<ImageScaleByNode>(
            (JArray)inputs["ref_videos.ref_video_0"]);
        Assert.Equal(0.25, scaled.ScaleBy.LiteralAsDouble());
        Assert.IsType<ImageFromBatchNode>(scaled.Image.Connection?.Node);
        Assert.Null(inputs["ref_video_audios.ref_video_audio_0"]);
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());

        live.AssertAllLive(referenceNode, scaled);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Continue_keeps_the_seam_when_context_exceeds_target_and_authored_videos_fill_limit()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject first = MakeClip(fixture.Stage());
        first["duration"] = 3.0;
        first["boundaryOut"] = Constants.BoundaryOutContinue;
        first["boundaryOutOverlap"] = 56;
        JObject second = MakeClip(fixture.Stage());
        second["duration"] = 1.0;
        second["references"] = new JArray(
            Enumerable.Range(0, 3).Select(index => MakeClipReference(
                "video",
                $"data:video/mp4;base64,VklERU8{index}",
                $"motion-{index}.mp4")));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(first, second),
            post => post["videofps"] = 30);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        SwarmVideoResampleFPSNode resample = bridge.NodeAt<SwarmVideoResampleFPSNode>(
            (JArray)inputs["ref_videos.ref_video_0"]);
        Assert.Equal(30, resample.FpsIn.LiteralAsDouble());
        Assert.Equal(24, resample.FpsOut.LiteralAsDouble());
        ImageFromBatchNode tail = Assert.IsType<ImageFromBatchNode>(
            resample.ImagesInput.Connection?.Node);
        Assert.Equal(58, tail.BatchIndex.LiteralAsInt());
        Assert.Equal(49, tail.Length.LiteralAsInt());
        TrimAudioDurationNode audio = bridge.NodeAt<TrimAudioDurationNode>(
            (JArray)inputs["ref_video_audios.ref_video_audio_0"]);
        Assert.Equal(107 / 30.0 - 39 / 24.0, audio.StartIndex.LiteralAsDouble().Value, 6);
        Assert.Equal(39 / 24.0, audio.Duration.LiteralAsDouble().Value, 6);
        Assert.NotNull(inputs["ref_videos.ref_video_1"]);
        Assert.NotNull(inputs["ref_videos.ref_video_2"]);
        Assert.Null(inputs["ref_videos.ref_video_3"]);

        live.AssertAllLive(referenceNode, resample, tail, audio);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Continue_end_aligns_reference_at_low_timeline_fps()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject first = MakeClip(fixture.Stage());
        first["duration"] = 2.0;
        first["boundaryOut"] = Constants.BoundaryOutContinue;
        first["boundaryOutOverlap"] = 5;
        JObject second = MakeClip(fixture.Stage());
        second["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(first, second),
            post => post["videofps"] = 6);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        ImageFromBatchNode aligned = bridge.NodeAt<ImageFromBatchNode>(
            (JArray)inputs["ref_videos.ref_video_0"]);
        Assert.Equal(3, aligned.BatchIndex.LiteralAsInt());
        Assert.Equal(5, aligned.Length.LiteralAsInt());
        SwarmVideoResampleFPSNode resample = Assert.IsType<SwarmVideoResampleFPSNode>(
            aligned.Image.Connection?.Node);
        ImageFromBatchNode sourceTail = Assert.IsType<ImageFromBatchNode>(
            resample.ImagesInput.Connection?.Node);
        Assert.Equal(20, sourceTail.BatchIndex.LiteralAsInt());
        Assert.Equal(2, sourceTail.Length.LiteralAsInt());
        TrimAudioDurationNode audio = bridge.NodeAt<TrimAudioDurationNode>(
            (JArray)inputs["ref_video_audios.ref_video_audio_0"]);
        Assert.Equal(22 / 6.0 - 5 / 24.0, audio.StartIndex.LiteralAsDouble().Value, 6);
        Assert.Equal(5 / 24.0, audio.Duration.LiteralAsDouble().Value, 6);

        live.AssertAllLive(referenceNode, aligned, resample, sourceTail, audio);
        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.5, true)]
    public async Task A_video_reference_is_resampled_to_the_model_rate_then_scaled_on_request(
        double scale,
        bool expectScaling)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["references"] = new JArray(
            MakeClipReference("video", "data:video/mp4;base64,TU9USU9O", "motion.mp4", mediaScale: scale));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        string videoInputNodeId =
            workflow[referenceNode.Id]["inputs"]["ref_videos.ref_video_0"][0].ToString();

        // The node reads a reference as a plain 24 fps batch, so the file's own rate is
        // resampled away before anything else touches the frames.
        SwarmVideoResampleFPSNode resampled = Assert.Single(
            bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        GetVideoComponentsNode components = Assert.IsType<GetVideoComponentsNode>(
            resampled.ImagesInput.Connection?.Node);
        Assert.Equal(24, resampled.FpsOut.LiteralAsDouble());
        Assert.Same(components, resampled.FpsIn.Connection?.Node);

        if (!expectScaling)
        {
            Assert.Empty(bridge.Graph.NodesOfType<ImageScaleByNode>());
            Assert.Equal(resampled.Id, videoInputNodeId);
        }
        else
        {
            ImageScaleByNode scaled = Assert.Single(
                bridge.Graph.NodesOfType<ImageScaleByNode>());
            Assert.Equal(videoInputNodeId, scaled.Id);
            Assert.Equal(scale, scaled.ScaleBy.LiteralAsDouble());
            // Scaling runs on the frames that survived the resample, not the raw load.
            Assert.Same(resampled, scaled.Image.Connection?.Node);
            live.AssertLive(scaled);
        }
        // The soundtrack still comes off the untouched load.
        live.AssertLive(resampled);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The node pairs <c>ref_video_audio_N</c> with <c>ref_video_N</c> by name, so a video carrying
    /// no soundtrack has to leave its number open. Closing the gap would slide the next video's
    /// track onto the silent one and condition the wrong reference.
    /// </summary>
    [Fact]
    public async Task A_soundtrack_keeps_the_number_of_the_video_reference_it_belongs_to()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["references"] = new JArray(
            MakeClipReference("video", "data:video/mp4;base64,U0lMRU5U", "silent.mp4"),
            MakeClipReference(
                "video",
                "data:video/mp4;base64,U0NPUkVE",
                "scored.mp4",
                includeSoundtrack: true));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        JObject inputs = (JObject)workflow[referenceNode.Id]["inputs"];
        Assert.NotNull(inputs["ref_videos.ref_video_0"]);
        Assert.NotNull(inputs["ref_videos.ref_video_1"]);
        Assert.Null(inputs["ref_video_audios.ref_video_audio_0"]);
        Assert.NotNull(inputs["ref_video_audios.ref_video_audio_1"]);

        live.AssertLive(referenceNode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A Base2Edit edit stage is published rather than captured, so this is the reference source
    /// that reaches the conditioning without any capture hook of the extension's own.
    /// </summary>
    [Fact]
    public async Task A_base2edit_clip_reference_is_wired_into_the_reference_conditioning()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["references"] = new JArray(
            new JObject { ["kind"] = "image", ["source"] = "edit0" });

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraSteps: [PublishBase2EditImageStep(0)]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node published = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        ComfyNode referenceNode = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "MiniMaxH3ReferenceToVideo");
        Assert.Equal(
            published.Id,
            ((JObject)workflow[referenceNode.Id]["inputs"])["ref_images.ref_image_0"][0]
                .ToString());

        // The node replaces the positive text encode, so reaching the sampler is what proves the
        // published image was tokenized with the prompt rather than left dangling.
        Assert.Same(referenceNode, StageSampler(bridge, 0).Positive.Connection?.Node);

        live.AssertAllLive(published, referenceNode);
        AssertShippable(bridge, workflow, live);
    }


    private static WorkflowGenerator.WorkflowGenStep SeedControlNetReferenceSource(
        int index) =>
        new(g =>
        {
            UnitTestStubs.EnsureComfyControlNetParamsRegistered();
            T2IModelHandler handler = new() { ModelType = "ControlNet" };
            using WorkflowBridge bridge = BridgeSync.For(g);
            T2IModel model = TestStubModel.Create(
                handler,
                $"UnitTest_MiniMax_Reference_ControlNet_{index}.safetensors");
            g.UserInput.Set(T2IParamTypes.Controlnets[index].Strength, 0.8);
            g.UserInput.Set(T2IParamTypes.Controlnets[index].Model, model);
            GetVideoComponentsNode components = bridge.AddNode(
                new GetVideoComponentsNode(),
                "901");
            ControlNetLoaderNode loader = bridge.AddNode(
                new ControlNetLoaderNode().With(
                    ControlNetName: model.ToString(g.ModelFolderFormat)),
                "911");
            ControlNetApplyAdvancedNode apply = new();
            apply.ControlNet.ConnectTo(loader.CONTROLNET);
            apply.Image.ConnectTo(components.Images);
            bridge.AddNode(apply, "921");
        }, Constants.WorkflowStepPriority.ControlNetPreprocessors - 0.01);


}
