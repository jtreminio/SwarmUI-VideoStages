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

/// <summary>
/// MiniMax H3 audio: which source fills the entry joint latent, when audio drives clip length, and
/// what reaches the published save. H3 generates audio jointly with video, so every non-native
/// source has to be injected into a latent core already built.
/// </summary>
[Collection("VideoStagesTests")]
public class MiniMaxAudioContractTests
{
    /// <summary>Seeds the core ControlNet branches no MiniMax POST shape builds.</summary>
    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> SeedControlNetCoreBranches(
        int count) =>
    [
        new(g =>
        {
            UnitTestStubs.EnsureComfyControlNetParamsRegistered();
            T2IModelHandler handler = new() { ModelType = "ControlNet" };
            using WorkflowBridge bridge = BridgeSync.For(g);
            for (int index = 0; index < count; index++)
            {
                T2IModel model = TestStubModel.Create(
                    handler,
                    $"UnitTest_MiniMax_ControlNet_{index}.safetensors");
                g.UserInput.Set(T2IParamTypes.Controlnets[index].Strength, 0.8);
                g.UserInput.Set(T2IParamTypes.Controlnets[index].Model, model);
                GetVideoComponentsNode components = bridge.AddNode(
                    new GetVideoComponentsNode(),
                    $"90{index + 1}");
                ControlNetLoaderNode loader = bridge.AddNode(
                    new ControlNetLoaderNode().With(
                        ControlNetName: model.ToString(g.ModelFolderFormat)),
                    $"91{index + 1}");
                ControlNetApplyAdvancedNode apply = new();
                apply.ControlNet.ConnectTo(loader.CONTROLNET);
                apply.Image.ConnectTo(components.Images);
                bridge.AddNode(apply, $"92{index + 1}");
            }
        }, Constants.WorkflowStepPriority.ControlNetPreprocessors - 0.01),
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            for (int index = 0; index < count; index++)
            {
                VideoGraphHelpers.RemoveNode(g, bridge, $"92{index + 1}");
                VideoGraphHelpers.RemoveNode(g, bridge, $"91{index + 1}");
            }
        }, Constants.WorkflowStepPriority.ControlNetPreprocessors + 0.01),
    ];

    /// <summary>
    /// A masked audio latent with <c>SolidMask</c> value 0 is what "preserved" means: the sampler
    /// must not regenerate over the uploaded track.
    /// </summary>
    [Fact]
    public async Task Uploaded_audio_is_preserved_in_the_entry_joint_latent()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioSource"] = "Upload";
        clip["uploadedAudio"] = UploadedAudio();

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Equal("QUJD", upload.AudioBase64.LiteralAsString());

        VAEEncodeAudioNode encode = Assert.Single(
            bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection?.Node, upload.Id));

        SetLatentNoiseMaskNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        SolidMaskNode solidMask = Assert.IsType<SolidMaskNode>(mask.Mask.Connection?.Node);
        Assert.Equal(0, solidMask.Value.LiteralAsDouble());

        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(mask.LATENT, joint.AudioLatent.Connection);
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(joint, sampler.LatentImage.Connection?.Node);

        live.AssertAllLive(upload, encode, mask, joint, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Audio-derived length wires the latent's <c>length</c> to <c>SwarmAudioLengthToFrames</c>
    /// instead of a literal, moving the 17k+5 snap into the graph.
    /// </summary>
    [Fact]
    public async Task Uploaded_audio_can_drive_the_entry_joint_latent_length()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioSource"] = "Upload";
        clip["clipLengthFromAudio"] = true;
        clip["uploadedAudio"] = UploadedAudio();

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Equal(MiniMaxWorkflowFixture.Fps, lengthToFrames.FrameRate.LiteralAsDouble());
        Assert.Equal(17, lengthToFrames.FrameGrid.LiteralAsInt());
        Assert.Equal(5, lengthToFrames.FrameGridOrigin.LiteralAsInt());
        Assert.Equal(0, lengthToFrames.FrameCountOffset.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, lengthToFrames, upload.Id));

        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Same(lengthToFrames.Frames, latent.Length.Connection);
        Assert.Null(latent.Length.LiteralAsInt());

        // The encoded track is trimmed to the derived length, so the encode reads through the same
        // node the latent takes its length from rather than the raw upload.
        VAEEncodeAudioNode encode = Assert.Single(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection?.Node, lengthToFrames.Id));

        live.AssertAllLive(upload, lengthToFrames, latent, encode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The shared output is an LTXVSeparateAVLatent slot, and core's post-cleanup rewrites every
    /// such connection onto the upstream concat halves — the sharing must survive that.
    /// </summary>
    [Fact]
    public async Task Reuse_audio_shares_one_latent_audio_output_across_later_stages()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5));
        clip["duration"] = 1.0;
        clip["reuseAudio"] = true;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode third = StageSampler(bridge, 2);
        SwarmKSamplerNode fourth = StageSampler(bridge, 3);

        SetLatentNoiseMaskNode thirdMask = Assert.IsType<SetLatentNoiseMaskNode>(
            Assert.IsType<LTXVConcatAVLatentNode>(third.LatentImage.Connection?.Node)
                .AudioLatent.Connection?.Node);
        SetLatentNoiseMaskNode fourthMask = Assert.IsType<SetLatentNoiseMaskNode>(
            Assert.IsType<LTXVConcatAVLatentNode>(fourth.LatentImage.Connection?.Node)
                .AudioLatent.Connection?.Node);

        Assert.NotNull(thirdMask.Samples.Connection);
        Assert.Same(thirdMask.Samples.Connection, fourthMask.Samples.Connection);

        VAEDecodeAudioNode decode = Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
        Assert.Same(thirdMask.Samples.Connection, decode.Samples.Connection);

        live.AssertAllLive(third, fourth, decode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// No cleanup pass sweeps <c>TrimAudioDuration</c>, so the discarded source-audio branch must
    /// never be built.
    /// </summary>
    [Fact]
    public async Task Init_video_audio_replaced_by_an_upload_leaves_nothing_behind()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(control: 0.5));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };
        clip["audioSource"] = "Upload";
        clip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "replacement.wav",
        };

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.True(
            ReachesUpstream(bridge, sampler, upload.Id),
            "The uploaded audio does not reach the sampler.");
        Assert.True(
            ReachesUpstream(bridge, live.FinalVideoSave().Audio.Connection?.Node, upload.Id),
            "The uploaded audio does not reach the published save.");

        // The positive control is Init_video_refines_conformed_footage_and_its_audio, where the
        // same footage does build a start_index 1 trim of its own soundtrack.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => node.StartIndex.LiteralAsDouble() == 1);

        live.AssertAllLive(upload, sampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Native_audio_reaches_the_published_video_save()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        VAEDecodeAudioNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeAudioNode>());

        Assert.NotNull(save.Audio.Connection);
        Assert.True(
            ReachesUpstream(bridge, save.Audio.Connection.Node, audioDecode.Id),
            "The published save's audio input does not trace back to the native audio decode.");

        live.AssertLive(audioDecode);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// A ControlNet source's own soundtrack can set the clip length, which moves the 17k+5 snap out
    /// of the compiler and into the graph exactly as an uploaded track does.
    /// </summary>
    [Fact]
    public async Task ControlNet_audio_can_drive_the_entry_joint_latent_length()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioSource"] = MediaSource.ControlNet;
        clip["clipLengthFromAudio"] = true;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraSteps: SeedControlNetCoreBranches(1));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        GetVideoComponentsNode source = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Same(source.Audio, lengthToFrames.AudioInput.Connection);
        Assert.Equal(17, lengthToFrames.FrameGrid.LiteralAsInt());
        Assert.Equal(5, lengthToFrames.FrameGridOrigin.LiteralAsInt());

        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Same(lengthToFrames.Frames, latent.Length.Connection);

        VAEEncodeAudioNode encode = Assert.Single(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode, source.Id));
        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(joint, StageSampler(bridge, 0).LatentImage.Connection?.Node);

        live.AssertAllLive(lengthToFrames, latent, encode, joint);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// No capture and an ambiguous pair of captures both mean "no usable ControlNet audio". The
    /// request must continue on H3's native audio generation rather than refuse or silently build
    /// a conditioned latent from the wrong source.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Unusable_ControlNet_audio_warns_and_keeps_native_audio_generation(
        int capturedTracks)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioSource"] = MediaSource.ControlNet;
        clip["clipLengthFromAudio"] = true;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)),
                extraSteps: capturedTracks == 0
                    ? null
                    : SeedControlNetCoreBranches(capturedTracks));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("ControlNet audio") && warning.Contains("using silence"));

        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Equal(MiniMaxWorkflowFixture.GeneratedFrames, latent.Length.LiteralAsInt());
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(latent, sampler.LatentImage.Connection?.Node);

        // The refused captures are this test's own scaffolding standing in for core's ControlNet
        // chain; with none seeded (arm 0) this is the ordinary no-orphans assertion.
        Assert.All(
            live.OrphanNodes(),
            orphan => Assert.StartsWith($"{GetVideoComponentsNode.ClassType}#", orphan));

        live.AssertAllLive(latent, sampler);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// A sibling extension's AceStepFun track is another length source; the extension reaches it by
    /// its reserved decode node id, not by scanning for an audio node.
    /// </summary>
    [Fact]
    public async Task AceStepFun_audio_can_drive_the_entry_joint_latent_length()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioSource"] = "audio0";
        clip["clipLengthFromAudio"] = true;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraSteps: [PublishAceStepFunAudioTrackStep(0)]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        string aceDecodeId = AudioHandler.MakeAceStepFunDecodeId(0);
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.True(ReachesUpstream(
            bridge,
            lengthToFrames.AudioInput.Connection?.Node,
            aceDecodeId));

        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Same(lengthToFrames.Frames, latent.Length.Connection);

        VAEEncodeAudioNode encode = Assert.Single(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection?.Node, aceDecodeId));
        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(joint, StageSampler(bridge, 0).LatentImage.Connection?.Node);

        live.AssertAllLive(lengthToFrames, latent, encode, joint);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An AceStepFun track index nothing published must warn and fall back, not silently generate
    /// against a nonexistent node id.
    /// </summary>
    [Fact]
    public async Task Missing_AceStepFun_audio_warns_and_keeps_native_audio_generation()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioSource"] = "audio7";
        clip["clipLengthFromAudio"] = true;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("audio7")
                && warning.Contains("continuing without that source"));

        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        // The authored length stands, so it is a literal rather than a wired frame count.
        Assert.Equal(MiniMaxWorkflowFixture.GeneratedFrames, latent.Length.LiteralAsInt());
        Assert.Same(latent, StageSampler(bridge, 0).LatentImage.Connection?.Node);

        live.AssertLive(latent);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A timeline audio segment is projected onto whichever clip it overlaps. Its spans are
    /// authored against the timeline, so the graph must pad the track to the clip's opening and
    /// condition only the clip the span lands in.
    /// </summary>
    [Fact]
    public async Task Timeline_audio_uses_aligned_clip_windows_and_conditions_only_its_own_clip()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject first = MakeClip(1.0, fixture.Stage());
        JObject second = MakeClip(1.0, fixture.Stage());
        JObject document = MakeDocument(first, second);
        // 0.1s into the second clip, which opens at the first clip's 39 frames.
        document["audioTracks"] = new JArray(AudioTrack(
            "timeline-segment",
            0.5,
            "timeline.wav",
            AudioSpan(39.0 / 24.0 + 0.1, 0.2, 0.1)));

        JObject workflow = await fixture.GenerateAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(0.1, trim.StartIndex.LiteralAsDouble());
        Assert.Equal(0.2, trim.Duration.LiteralAsDouble().Value, 8);

        // Two silences: the lead-in inside the clip, and the whole first clip the span skips.
        double?[] silences = [.. bridge.Graph.NodesOfType<EmptyAudioNode>()
            .Select(node => node.Duration.LiteralAsDouble())
            .Order()];
        Assert.Equal(2, silences.Length);
        Assert.Equal(0.1, silences[0].Value, 8);
        Assert.Equal(39.0 / 24.0, silences[1].Value, 8);

        VAEEncodeAudioNode encode = Assert.Single(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection?.Node, upload.Id));

        // A windowed mask, not the whole-track preserve mask an uploaded clip audio would build.
        Assert.Empty(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        SwarmSetAudioMaskWindowsNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        JObject window = Assert.IsType<JObject>(
            Assert.Single(JArray.Parse(mask.Windows.LiteralAsString())));
        Assert.Equal(0.1, window.Value<double>("start"));
        Assert.Equal(0.3, window.Value<double>("end"));

        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(mask.Latent, joint.AudioLatent.Connection);
        SwarmKSamplerNode conditioned = StageSampler(bridge, 1);
        SwarmKSamplerNode unconditioned = StageSampler(bridge, 0);
        Assert.Same(joint, conditioned.LatentImage.Connection?.Node);
        Assert.False(ReachesUpstream(bridge, unconditioned, upload.Id));

        live.AssertAllLive(upload, trim, encode, mask, conditioned, unconditioned);
        AssertShippable(bridge, workflow, live);
    }
}
