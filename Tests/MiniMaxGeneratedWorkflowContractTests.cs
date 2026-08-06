using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Execution.Audio;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// MiniMax H3 generated-graph contracts. Stages are identified by sampler seed because node order
/// and upstream reachability do not identify a refining stage.
/// </summary>
[Collection("VideoStagesTests")]
public class MiniMaxGeneratedWorkflowContractTests
{
    [Fact]
    public async Task Basic_text_to_video_can_be_generated_from_the_Comfy_API_POST_body()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        VAEDecodeAudioNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeAudioNode>());

        Assert.Same(latent, sampler.LatentImage.Connection?.Node);
        Assert.Equal(MiniMaxWorkflowFixture.Steps, sampler.Steps.LiteralAsInt());

        Assert.Equal(39, latent.Length.LiteralAsInt());

        live.AssertAllLive(latent, sampler, audioDecode);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// A refine stage re-encodes the decoded video, which collapses back onto the sampler's own
    /// latent and leaves stage 0's decode unused for core's cleanup to prune — the audio half must
    /// survive that.
    /// </summary>
    [Fact]
    public async Task A_second_stage_refines_the_joint_latent_that_survives_core_cleanup()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(), fixture.Stage(control: 0.5));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count());
        Assert.Equal(0, StageSampler(bridge, 0).StartAtStep.LiteralAsInt());

        SwarmKSamplerNode refine = StageSampler(bridge, 1);
        Assert.Equal(4, refine.StartAtStep.LiteralAsInt());
        LTXVConcatAVLatentNode joint = Assert.IsType<LTXVConcatAVLatentNode>(
            refine.LatentImage.Connection?.Node);

        SetLatentNoiseMaskNode audioMask = Assert.IsType<SetLatentNoiseMaskNode>(
            joint.AudioLatent.Connection?.Node);
        SolidMaskNode solidMask = Assert.IsType<SolidMaskNode>(audioMask.Mask.Connection?.Node);
        Assert.Equal(0, solidMask.Value.LiteralAsDouble());

        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
        Assert.Single(bridge.Graph.NodesOfType<SolidMaskNode>());
        // The refine stage masks the carried audio rather than re-encoding it.
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());

        live.AssertAllLive(joint, audioMask, solidMask, refine);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Three places emit a SwarmTrimFrames node: core's save step, core's image-to-video step,
    /// and the extension's global trimmer. Only the production list runs all three.
    /// </summary>
    [Fact]
    public async Task Three_stages_publish_intermediates_and_trim_only_the_final_output()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5),
            fixture.Stage("PreviousStage", control: 0.25));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post =>
            {
                post["outputintermediateimages"] = true;
                post["trimvideostartframes"] = 4;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        SwarmKSamplerNode third = StageSampler(bridge, 2);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, trim, third.Id),
            "The trim does not sit downstream of the final stage.");

        SwarmSaveAnimationWSNode finalSave = live.FinalVideoSave();
        Assert.True(
            ReachesUpstream(bridge, finalSave.Images.Connection?.Node, trim.Id),
            "The published save does not read the trimmed output.");

        SwarmSaveAnimationWSNode[] saves =
            [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, first.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, second.Id));
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, second.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, third.Id));
        Assert.All(
            saves.Where(save => !ReferenceEquals(save, finalSave)),
            save => Assert.False(
                ReachesUpstream(bridge, save.Images.Connection?.Node, trim.Id),
                "An intermediate save reads the trimmed output; only the final one should."));

        live.AssertAllLive(first, second, third, trim);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Core's model-gen step picks the LoRA confinement by refiner/pixel-decoder/image-to-video
    /// state; only the production list exercises that branch.
    /// <para>
    /// The loader cache is the mechanism: a stage that loads LoRAs must publish its own loader
    /// chain under the shared <c>modelloader_*</c> key, so the next stage reuses the plain loader
    /// rather than inheriting the previous stage's LoRAs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Persisted_and_prompt_LoRAs_are_stage_scoped()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_MiniMax_Prompt.safetensors")
            .InstallModel("LoRA", "UnitTest_MiniMax_Persisted.safetensors");

        JObject first = fixture.Stage();
        first["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_MiniMax_Persisted",
            ["weight"] = 0.6,
            ["textEncoderWeight"] = 0.7,
        });
        JObject clip = MakeClip(first, fixture.Stage("PreviousStage", control: 0.5, steps: 9));
        clip["duration"] = 1.0;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(clip),
                    post => post["prompt"] =
                        "global <videoclip[0,0]><lora:UnitTest_MiniMax_Prompt:0.4:0.8>"));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode secondSampler = StageSampler(bridge, 1);
        Assert.Equal(MiniMaxWorkflowFixture.Steps, firstSampler.Steps.LiteralAsInt());
        Assert.Equal(9, secondSampler.Steps.LiteralAsInt());

        ComfyNode[] loras = [.. LoraLoaderNodesOf(bridge)];
        Assert.Equal(2, loras.Length);
        Assert.All(loras, lora =>
        {
            Assert.True(
                ReachesUpstream(bridge, firstSampler.Model.Connection?.Node, lora.Id),
                "A stage-scoped LoRA is missing from the first stage's model chain.");
            Assert.False(
                ReachesUpstream(bridge, secondSampler.Model.Connection?.Node, lora.Id),
                "A stage-scoped LoRA leaked onto the second stage's model chain.");
        });

        AssertModelOnlyLora(loras, "UnitTest_MiniMax_Persisted.safetensors", 0.6);
        AssertModelOnlyLora(loras, "UnitTest_MiniMax_Prompt.safetensors", 0.4);

        // The shared loader cache is what the next stage reads; leaving stage 0's LoRA chain in it
        // is how a stage-scoped LoRA would leak forward.
        Assert.True(generator.NodeHelpers.TryGetValue(
            $"modelloader_{fixture.Model.Name}_image2video",
            out string cachedLoader));
        Assert.Equal(secondSampler.Model.Connection?.Node.Id, cachedLoader.Split(':')[0]);

        live.AssertAllLive([firstSampler, secondSampler, .. loras]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Without <c>comfy_loadimage_b64</c> core loads the upload as a bare image with no attached
    /// audio and no FPS reference, so only this path exercises the conform chain end to end.
    /// </summary>
    [Fact]
    public async Task Init_video_refines_conformed_footage_and_its_audio()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Equal(24, window.StartFrame.LiteralAsInt());
        Assert.Equal(39, window.FrameCount.LiteralAsInt());

        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == window.Id);
        Assert.Equal(MiniMaxWorkflowFixture.Width, scale.Width.LiteralAsInt());
        Assert.Equal(MiniMaxWorkflowFixture.Height, scale.Height.LiteralAsInt());

        TrimAudioDurationNode sourceAudio = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => node.StartIndex.LiteralAsDouble() == 1);
        Assert.Equal(39 / 24.0, sourceAudio.Duration.LiteralAsDouble().Value, 6);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count());
        Assert.Equal(4, first.StartAtStep.LiteralAsInt());
        Assert.Equal(4, second.StartAtStep.LiteralAsInt());

        // The refine stage inherits both through stage 0's latent, so asserting them on stage 1
        // too would be true by transitivity.
        Assert.True(ReachesUpstream(bridge, first, window.Id));
        Assert.True(ReachesUpstream(bridge, first, sourceAudio.Id));
        Assert.Single(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());

        // One joint latent per stage survives core's post-cleanup, which collapses
        // LTXVSeparateAVLatent over LTXVConcatAVLatent and prunes unused concats.
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>().Count());

        live.AssertAllLive(window, scale, sourceAudio, first, second);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

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
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
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
    /// Framing reuse is keyed on source plus size plus method, so a second stage resolution adds
    /// a second SwarmFrameImage — and each must reach its own stage.
    /// </summary>
    [Fact]
    public async Task A_final_frame_reference_is_reframed_for_each_stage_resolution()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5));
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(UploadedReference("TEFTVA==", fromEnd: true));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());

        SwarmFrameImageNode framed512 = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameImageNode>(),
            node => node.Width.LiteralAsInt() == 512);
        SwarmFrameImageNode framed768 = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameImageNode>(),
            node => node.Width.LiteralAsInt() == 768);
        Assert.All(
            new[] { framed512, framed768 },
            node =>
            {
                Assert.Same(upload, node.ImagesInput.Connection?.Node);
                Assert.Equal("fit-green", node.Method.LiteralAsString());
            });

        // The authored reference is fromEnd, so each stage must consume its framing through the
        // keyframe node's last_frame slot — reachability from the sampler alone would not say which
        // slot, nor that first_frame stayed unwired.
        Assert.All(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>(),
            keyframes => Assert.Null(keyframes.FirstFrame.Connection));
        Assert.Equal(
            [512, 768],
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>()
                .Select(keyframes => Assert.IsType<SwarmFrameImageNode>(
                    keyframes.LastFrame.Connection?.Node).Width.LiteralAsInt())
                .Order());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.True(ReachesUpstream(bridge, first.Positive.Connection?.Node, framed512.Id));
        Assert.True(ReachesUpstream(bridge, second.Positive.Connection?.Node, framed768.Id));

        live.AssertAllLive(upload, framed512, framed768, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A reference the compiler drops must leave no loader behind; the diagnostic alone does not
    /// prove the upload never reached the graph.
    /// </summary>
    [Fact]
    public async Task Authored_frame_uploads_are_framed_and_an_ignored_reference_loads_nothing()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(
            UploadedReference("RklSU1Q=", fromEnd: false),
            UploadedReference("TEFTVA==", fromEnd: true),
            UploadedReference("SUdOT1JF", fromEnd: false, frame: 9));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["height"] = 320);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node[] uploads =
            [.. bridge.Graph.NodesOfType<SwarmLoadImageB64Node>()];
        Assert.Equal(2, uploads.Length);
        Assert.DoesNotContain(
            uploads,
            node => node.ImageBase64.LiteralAsString() == "SUdOT1JF");

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        SwarmFrameImageNode firstFrame = Assert.IsType<SwarmFrameImageNode>(
            keyframes.FirstFrame.Connection?.Node);
        SwarmFrameImageNode lastFrame = Assert.IsType<SwarmFrameImageNode>(
            keyframes.LastFrame.Connection?.Node);
        Assert.All(
            new[] { firstFrame, lastFrame },
            node =>
            {
                Assert.Equal("fit-green", node.Method.LiteralAsString());
                Assert.Equal(512, node.Width.LiteralAsInt());
                Assert.Equal(320, node.Height.LiteralAsInt());
            });

        // Which upload lands in which slot: two framed images of identical size would otherwise be
        // interchangeable.
        Assert.Equal(
            "RklSU1Q=",
            Assert.IsType<SwarmLoadImageB64Node>(firstFrame.ImagesInput.Connection?.Node)
                .ImageBase64.LiteralAsString());
        Assert.Equal(
            "TEFTVA==",
            Assert.IsType<SwarmLoadImageB64Node>(lastFrame.ImagesInput.Connection?.Node)
                .ImageBase64.LiteralAsString());
        Assert.Same(keyframes, StageSampler(bridge, 0).Positive.Connection?.Node);

        live.AssertAllLive([keyframes, firstFrame, lastFrame, .. uploads]);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Two_clips_cut_together_into_one_published_video()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject shortClip = MakeClip(fixture.Stage());
        shortClip["duration"] = 0.2;
        JObject longClip = MakeClip(fixture.Stage());
        longClip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(shortClip, longClip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // ceil(s*fps)+1 gives 6 and 25 frames, which snap up to 22 and 39 on the 17k+5 grid.
        Assert.Equal(
            [22, 39],
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>()
                .Select(node => node.Length.LiteralAsInt())
                .Order());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, first.Id));
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, second.Id));
        Assert.Equal(MiniMaxWorkflowFixture.Fps, save.Fps.LiteralAsDouble());

        live.AssertAllLive(first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The crossfade blend must be what the published save reads, not a parallel branch.
    /// </summary>
    [Fact]
    public async Task Two_clips_crossfade_through_the_shared_decoded_merge()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject shortClip = MakeClip(fixture.Stage());
        shortClip["duration"] = 0.2;
        shortClip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        shortClip["boundaryOutOverlap"] = 8;
        JObject longClip = MakeClip(fixture.Stage());
        longClip["duration"] = 1.0;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(shortClip, longClip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmRampMaskBatchNode ramp = Assert.Single(
            bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(8, ramp.Frames.LiteralAsInt());
        ImageCompositeMaskedNode blend = Assert.Single(
            bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        Assert.Same(ramp, blend.Mask.Connection?.Node);

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection?.Node, blend.Id),
            "The published video does not read the crossfade merge.");

        // The overlap is consumed, not appended: 22 + 39 frames merged over 8.
        Assert.Equal(53, generator.CurrentMedia.Frames);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);

        live.AssertAllLive(ramp, blend);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The carry crosses a VAE round trip, which core's post-cleanup collapses.
    /// </summary>
    [Fact]
    public async Task Crossfade_audio_carry_conditions_the_next_clip()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject shortClip = MakeClip(fixture.Stage());
        shortClip["duration"] = 0.2;
        shortClip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        shortClip["boundaryOutOverlap"] = 8;
        shortClip["boundaryOutCarryAudio"] = true;
        JObject longClip = MakeClip(fixture.Stage());
        longClip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(shortClip, longClip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmSetAudioMaskWindowsNode carryMask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        // The carry conditions the SECOND clip from the FIRST clip's tail, not the other way round.
        Assert.True(ReachesUpstream(bridge, second, carryMask.Id));
        Assert.False(ReachesUpstream(bridge, first, carryMask.Id));
        Assert.True(ReachesUpstream(bridge, carryMask, first.Id));

        // The preserved window is the 8-frame overlap at the head of the second clip, and the
        // carried track is cut from the tail of the first clip's 22 frames.
        JObject window = Assert.IsType<JObject>(
            Assert.Single(JArray.Parse(carryMask.Windows.LiteralAsString())));
        Assert.Equal(0, window.Value<double>("start"));
        Assert.Equal(0.33, window.Value<double>("end"), 6);
        // The graph also carries the merge's own tail trim, so select the carried cut by the mask
        // that reads it rather than by the values under test.
        TrimAudioDurationNode carryTrim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => ReachesUpstream(bridge, carryMask.Samples.Connection?.Node, node.Id));
        Assert.Equal(14 / 24.0, carryTrim.StartIndex.LiteralAsDouble().Value, 6);
        Assert.Equal(8 / 24.0, carryTrim.Duration.LiteralAsDouble().Value, 6);

        live.AssertAllLive(carryMask, carryTrim, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// No stage generates, so the frame count stays authored (25) instead of snapping to the
    /// 17k+5 grid.
    /// </summary>
    [Fact]
    public async Task Init_video_passthrough_publishes_conformed_footage_unsampled()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(control: 0));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Equal(24, window.StartFrame.LiteralAsInt());
        Assert.Equal(25, window.FrameCount.LiteralAsInt());

        ImageScaleNode conform = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == window.Id);
        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, conform.Id));

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            node => ReachesUpstream(bridge, save.Images.Connection?.Node, node.Id));
        Assert.Empty(bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());

        // The handoff the next clip or stage would read: unsnapped frame count, and the source's
        // own audio still attached rather than a generated track.
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath((JArray)generator.CurrentMedia.Path)?.Node,
            window.Id));
        Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath((JArray)generator.CurrentMedia.AttachedAudio.Path)?.Node);

        live.AssertAllLive(window, conform);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
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
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Nothing between the base capture (-4.2) and the refiner capture (5.89) touches CurrentMedia
    /// unless core's refiner (-4) and decode (1) run, so only the production list can tell a
    /// "Refiner" reference from a "Base" one.
    /// </summary>
    [Theory]
    [InlineData("Refiner")]
    [InlineData("Base")]
    public async Task A_host_frame_reference_resolves_to_its_own_stage(string source)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(MakeRef(source));

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

        SwarmKSamplerNode baseSampler = fixture.BaseSampler(bridge);
        SwarmKSamplerNode refinerSampler = fixture.RefinerSampler(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        ComfyNode firstFrame = keyframes.FirstFrame.Connection?.Node;
        Assert.NotNull(firstFrame);
        AssertImageSource(firstFrame, "first_frame");

        Assert.Equal(
            source == "Refiner",
            ReachesUpstream(bridge, firstFrame, refinerSampler.Id));
        Assert.True(ReachesUpstream(bridge, firstFrame, baseSampler.Id));

        live.AssertLive(keyframes);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Text-to-video is the shape where the base capture is H3's own joint audio/video latent
    /// rather than a decoded image, so it is the shape that proves the capture is decoded before
    /// it reaches a keyframe input. Handed over raw, it fails the whole request on a LATENT wired
    /// into an IMAGE — no video, and no warning saying why.
    /// </summary>
    [Fact]
    public async Task A_text_to_video_base_frame_reference_is_decoded_before_it_is_keyframed()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(MakeRef("Base"));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        ComfyNode firstFrame = keyframes.FirstFrame.Connection?.Node;
        Assert.NotNull(firstFrame);
        AssertImageSource(firstFrame, "first_frame");

        live.AssertLive(keyframes);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A mis-dispatch is invisible at plan level and the frontend offers <c>latentmodel-*</c> by
    /// default; the danger is a pixel upscale silently substituted.
    /// </summary>
    [Fact]
    public async Task Latent_model_upscale_emits_no_upscale_nodes()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0,
                upscale: 1.5,
                upscaleMethod: Fixtures.LtxV23SpatialUpscaler));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Empty(bridge.Graph.NodesOfType<UpscaleModelLoaderNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageUpscaleWithModelNode>());

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Equal(MiniMaxWorkflowFixture.Width, latent.Width.LiteralAsInt());
        Assert.Equal(MiniMaxWorkflowFixture.Height, latent.Height.LiteralAsInt());

        live.AssertAllLive(sampler, latent);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Core's post-cleanup deletes a <c>start_at_step &gt;= steps</c> SwarmKSampler and rewires past
    /// it; the upscale must survive that rewire on the live path.
    /// </summary>
    [Fact]
    public async Task A_zero_control_latent_upscale_still_scales_the_joint_latent()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0,
                upscale: 1.5,
                upscaleMethod: "latent-bislerp"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LatentUpscaleByNode scale = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", scale.UpscaleMethod.LiteralAsString());
        Assert.Equal(1.5, scale.ScaleBy.LiteralAsDouble());

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 768);

        live.AssertLive(scale);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// StartStep = <c>floor(steps * (1 - control))</c>.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(0.5, 4)]
    [InlineData(0.25, 6)]
    public async Task A_refine_stage_starts_at_the_control_derived_step(
        double control,
        int expectedStartStep)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(), fixture.Stage("PreviousStage", control: control));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode refine = StageSampler(bridge, 1);
        Assert.Equal(expectedStartStep, refine.StartAtStep.LiteralAsInt());
        Assert.Equal(MiniMaxWorkflowFixture.Steps, refine.Steps.LiteralAsInt());

        live.AssertLive(refine);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// An Error severity must travel ThrowIfBlocking → SwarmUserErrorException → the API's error
    /// response. Nothing else proves a blocking diagnostic stops the request.
    /// </summary>
    [Fact]
    public async Task A_blocking_diagnostic_rejects_the_request()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };
        clip["audioSource"] = "Upload";
        clip["clipLengthFromAudio"] = true;
        clip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "clip.wav",
        };

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => fixture.GenerateAsync(MakeDocument(clip)));
        Assert.Contains("audio", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Core's image-to-video step (11) builds its own MiniMax graph, which DropCoreOutput (11.05)
    /// must remove before the extension builds the timeline.
    /// </summary>
    [Fact]
    public async Task Image_to_video_drops_the_core_built_graph_and_keeps_one_timeline()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());

        live.AssertLive(latent);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// The base capture (-4.2) precedes core's decode (1), so it is a latent; it must reach the
    /// keyframe chain through a VAE decode, not raw.
    /// </summary>
    [Fact]
    public async Task Base_frame_reference_never_feeds_a_latent_into_the_keyframe_chain()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(MakeRef("Base"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        Assert.NotNull(keyframes.FirstFrame.Connection);
        Assert.Contains(
            bridge.Graph.NodesOfType<IVaeDecode>(),
            decode => ReachesUpstream(bridge, keyframes.FirstFrame.Connection.Node, decode.Id));

        AssertImageSource(keyframes.FirstFrame.Connection?.Node, "first_frame");
        // Only a first-frame reference was authored, so last_frame must be genuinely unwired
        // rather than silently skipped by the image-source check.
        Assert.Null(keyframes.LastFrame.Connection);
        foreach (SwarmFrameImageNode framed in bridge.Graph.NodesOfType<SwarmFrameImageNode>())
        {
            AssertImageSource(framed.ImagesInput.Connection?.Node, "SwarmFrameImage.images");
        }

        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
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

    /// <summary>
    /// <c>RootPlanCompiler</c> keeps the host root for an image-to-video request with a generated
    /// clip, and core's cleanup whitelist excludes checkpoint loaders and samplers — so an unused
    /// base generation would survive.
    /// </summary>
    [Fact]
    public async Task Image_to_video_with_an_uploaded_first_frame_builds_no_unused_base_generation()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["frameRefs"] = new JArray(UploadedReference("RklSU1Q=", fromEnd: false));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        live.AssertLive(upload);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
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
    /// A pixel upscale scales the previous stage's decoded frames, so the next stage's joint latent
    /// is re-encoded from the scaled image rather than from a scaled latent.
    /// </summary>
    [Fact]
    public async Task A_pixel_upscale_resizes_the_decoded_frames_before_the_next_stage()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: "pixel-lanczos"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 768);
        Assert.Equal(768, scale.Height.LiteralAsInt());
        Assert.Equal("lanczos", scale.UpscaleMethod.LiteralAsString());
        Assert.True(
            ReachesUpstream(bridge, scale, first.Id),
            "The upscale does not read the previous stage's output.");
        Assert.True(
            ReachesUpstream(bridge, JointLatentOf(second).VideoLatent.Connection?.Node, scale.Id),
            "The next stage's video latent is not built from the upscaled frames.");

        // Nothing is scaled in latent space; the latent sibling above is the positive control.
        Assert.Empty(bridge.Graph.NodesOfType<LatentUpscaleByNode>());

        live.AssertAllLive(first, second, scale);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Latent interpolation scales the video half in latent space and leaves the audio half — which
    /// shares the same joint latent — untouched. The zero-control sibling only proves the node
    /// survives core's sampler deletion; this proves a refining stage samples the scaled latent.
    /// </summary>
    [Fact]
    public async Task A_latent_interpolation_upscale_resizes_only_the_video_half()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latent-bislerp"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        LatentUpscaleByNode scale = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", scale.UpscaleMethod.LiteralAsString());
        // 2.0, not the authored-elsewhere 1.5: LatentUpscaleBy's generated default IS 1.5.
        Assert.Equal(2.0, scale.ScaleBy.LiteralAsDouble());
        Assert.True(ReachesUpstream(bridge, scale, first.Id));

        LTXVConcatAVLatentNode joint = JointLatentOf(second);
        Assert.Same(scale.LATENT, joint.VideoLatent.Connection);
        Assert.NotSame(scale.LATENT, joint.AudioLatent.Connection);

        // No pixel round trip: nothing is scaled to the upscaled edge in image space.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 1024);
        Assert.NotNull(live.PublishedAudio());

        live.AssertAllLive(first, second, scale);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A model upscale runs an ESRGAN-style model over the decoded frames and then fits the result
    /// to the stage resolution, because the model's own factor is fixed and need not match the
    /// authored one.
    /// </summary>
    [Fact]
    public async Task A_model_upscale_fits_its_output_to_the_next_stage_resolution()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: $"model-{UpscaleModelFileName}"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        UpscaleModelLoaderNode loader = Assert.Single(
            bridge.Graph.NodesOfType<UpscaleModelLoaderNode>());
        Assert.Equal(UpscaleModelFileName, loader.ModelName.LiteralAsString());
        ImageUpscaleWithModelNode modelUpscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageUpscaleWithModelNode>());
        Assert.Same(loader, modelUpscale.UpscaleModel.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, modelUpscale, first.Id));

        ImageScaleNode fit = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => ReferenceEquals(node.Image.Connection?.Node, modelUpscale));
        Assert.Equal(768, fit.Width.LiteralAsInt());
        Assert.Equal(768, fit.Height.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, JointLatentOf(second).VideoLatent.Connection?.Node, fit.Id),
            "The next stage's video latent is not built from the fitted upscale.");

        live.AssertAllLive(first, second, loader, modelUpscale, fit);
        AssertShippable(bridge, workflow, live);
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
        clip["audioSource"] = Constants.AudioSourceControlNet;
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
        clip["audioSource"] = Constants.AudioSourceControlNet;
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
            extraSteps: [SeedAceStepFunAudioTrack(0)]);
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

    /// <summary>Nothing resolves or downloads this; the loader only names it.</summary>
    private const string UpscaleModelFileName = "unit-test-upscaler.pth";

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

    /// <summary>Stands in for the sibling extension that publishes AceStepFun tracks.</summary>
    private static WorkflowGenerator.WorkflowGenStep SeedAceStepFunAudioTrack(int trackIndex) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            bridge.AddNode(
                new VAEDecodeAudioNode(),
                AudioHandler.MakeAceStepFunDecodeId(trackIndex));
        }, 11.05);
}
