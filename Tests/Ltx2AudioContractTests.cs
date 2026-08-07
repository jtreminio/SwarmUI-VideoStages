using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// LTX-2 audio: how a clip's chosen track reaches the joint audio/video latent, how it is masked so
/// the model preserves it instead of regenerating it, and how per-clip audio is published — all
/// generated through the real Comfy API POST path.
/// <para>
/// Shape is load-bearing. Most tests use image-to-video, where core's own root chain is what
/// generates and the extension conditions it in place. Under a discarded text root the first clip's
/// stage replaces that chain, so its audio takes the same windowed-latent path every later clip
/// takes — see
/// <see cref="A_text_root_clips_timeline_segment_conditions_the_stage_that_replaces_the_root"/>.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class Ltx2AudioContractTests
{
    private static JObject AudioClip(
        string audioSource,
        double? duration,
        JObject uploadedAudio,
        params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        clip["audioSource"] = audioSource;
        if (duration is not null)
        {
            clip["duration"] = duration.Value;
        }
        if (uploadedAudio is not null)
        {
            clip["uploadedAudio"] = uploadedAudio;
        }
        return clip;
    }

    private static LTXVAudioVAEDecodeNode PublishedAudioDecode(WorkflowLivePath live) =>
        Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio());

    /// <summary>The single window a preserve-mask node carries.</summary>
    private static (double Start, double End) OnlyWindowOf(SwarmSetAudioMaskWindowsNode mask)
    {
        JObject window = Assert.IsType<JObject>(
            Assert.Single(JArray.Parse(mask.Windows.LiteralAsString())));
        return ((double)window["start"], (double)window["end"]);
    }

    /// <summary>
    /// An uploaded clip track is encoded, wrapped in a fully-preserving noise mask and handed to the
    /// stage's joint latent, so the model keeps it verbatim. Requesting <c>Upload</c> with nothing
    /// uploaded must not fabricate any of that — the clip falls back to model-generated audio.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uploaded_clip_audio_is_conditioned_only_when_a_payload_is_present(
        bool hasPayload)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = AudioClip(
            MediaSource.Upload,
            duration: 1.0,
            hasPayload ? UploadedAudio("clip.wav") : null,
            fixture.Stage(control: 0.5));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        LTXVAudioVAEDecodeNode published = PublishedAudioDecode(live);
        // Length-matching is a separate opt-in; a plain upload must not resize the video.
        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());

        if (!hasPayload)
        {
            Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
            Assert.Empty(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
            Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
            // Positive control: the clip still generates, and its audio is the model's own.
            Assert.IsType<LTXVEmptyLatentAudioNode>(
                JointLatentOf(sampler).AudioLatent.Connection?.Node);
        }
        else
        {
            SwarmLoadAudioB64Node upload = Assert.Single(
                bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
            LTXVAudioVAEEncodeNode encode = Assert.Single(
                bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
            SwarmEnsureAudioNode ensure = Assert.IsType<SwarmEnsureAudioNode>(
                encode.Audio.Connection?.Node);
            Assert.Same(upload.AUDIO, ensure.Audio.Connection);

            SetLatentNoiseMaskNode mask = Assert.Single(
                bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
            Assert.Same(encode.AudioLatent, mask.Samples.Connection);
            // Value 0 is "regenerate nothing": the whole track is preserved.
            Assert.Equal(0.0, Assert.IsType<SolidMaskNode>(mask.Mask.Connection?.Node)
                .Value.LiteralAsDouble());
            Assert.Same(mask.LATENT, JointLatentOf(sampler).AudioLatent.Connection);
            Assert.True(ReachesUpstream(bridge, published, upload.Id));
        }

        Assert.True(ReachesUpstream(bridge, published, sampler.Id));
        live.AssertAllLive(sampler, published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// <c>clipLengthFromAudio</c> makes the uploaded track decide the clip length: the frame count
    /// is a wire off <c>SwarmAudioLengthToFrames</c> rather than a literal, and both the video
    /// latent and the audio the model conditions on come from that same node so they cannot drift.
    /// </summary>
    // 24 is also SwarmAudioLengthToFrames' codegen default, so the 30 arm is what proves the
    // timeline rate is what production writes into frame_rate.
    [Theory]
    [InlineData(VideoStagesWorkflowFixture.Fps)]
    [InlineData(30)]
    public async Task Clip_length_from_audio_drives_the_empty_latent_length_from_the_uploaded_track(
        int rate)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = AudioClip(
            MediaSource.Upload,
            duration: 1.0,
            UploadedAudio("clip.wav"),
            fixture.Stage(control: 0.5));
        clip["clipLengthFromAudio"] = true;

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(clip),
            post => post["videofps"] = rate);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Equal(rate, lengthToFrames.FrameRate.LiteralAsInt());
        // The rate is all LTX chooses: unlike MiniMax, it never passes a grid, so the 8k+1 snap is
        // whatever SwarmAudioLengthToFrames itself defaults to. That silent coupling is the claim —
        // the inherited default must still be the grid LTX declares, or clips sized from audio
        // land off the grid every other part of the architecture snaps to. (Nothing here can prove
        // the inputs are honoured, since LTX never writes them; MiniMax's
        // Uploaded_audio_can_drive_the_entry_joint_latent_length is the control that does.)
        Assert.Equal(
            Ltx2ArchitectureModule.FrameGrid,
            lengthToFrames.FrameGrid.LiteralAsInt());
        Assert.Equal(1, lengthToFrames.FrameGridOrigin.LiteralAsInt());
        Assert.Equal(1, lengthToFrames.FrameCountOffset.LiteralAsInt());

        // The measured track is the padded one, not the raw upload: measuring the raw upload would
        // report a length the graph never generates at.
        SwarmEnsureAudioNode measured = Assert.IsType<SwarmEnsureAudioNode>(
            lengthToFrames.AudioInput.Connection?.Node);
        Assert.Same(upload.AUDIO, measured.Audio.Connection);

        EmptyLTXVLatentVideoNode emptyVideo = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Same(lengthToFrames.Frames, emptyVideo.Length.Connection);
        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.True(ReachesUpstream(bridge, encode, lengthToFrames.Id));

        LTXVAudioVAEDecodeNode published = PublishedAudioDecode(live);
        Assert.True(ReachesUpstream(bridge, published, upload.Id));

        live.AssertAllLive(lengthToFrames, emptyVideo, StageSampler(bridge, 0), published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A sourced clip on <c>Native</c> audio takes the footage's own track: it is encoded and
    /// masked into the joint latent the stage samples, and the published audio is what came back
    /// out of that stage — not the pre-stage latent it was built from.
    /// </summary>
    [Fact]
    public async Task Native_clip_audio_is_masked_into_the_stage_latent_and_republished_from_it()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = AudioClip(
            MediaSource.Native,
            duration: 1.0,
            uploadedAudio: null,
            fixture.Stage(control: 0.5));
        clip["initVideo"] = SourceVideo();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Not StageSampler: an initVideo clip keeps core's image-to-video root sampler, which also
        // seeds Seed + 42. Reachability to the source footage is unambiguous.
        SwarmFrameWindowNode sourceWindow = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        SwarmKSamplerNode stage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => ReachesUpstream(bridge, sampler, sourceWindow.Id));

        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.True(ReachesUpstream(bridge, encode, components.Id));

        SetLatentNoiseMaskNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        Assert.Same(encode.AudioLatent, mask.Samples.Connection);
        Assert.Same(mask.LATENT, JointLatentOf(stage).AudioLatent.Connection);

        LTXVAudioVAEDecodeNode published = PublishedAudioDecode(live);
        Assert.True(
            ReachesUpstream(bridge, published, stage.Id),
            "The published audio decode does not read this stage's output.");
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);

        live.AssertAllLive(stage, encode, mask, published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Stage 1 continues from the audio latent stage 0 sampled, straight off the split. The counts
    /// are the point: one encode and one mask for the whole clip means the handoff did not decode,
    /// re-encode and re-mask audio that was already in latent space.
    /// </summary>
    [Fact]
    public async Task Multi_stage_clip_hands_the_audio_latent_between_stages_without_re_encoding()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = AudioClip(
            MediaSource.Upload,
            duration: 1.0,
            UploadedAudio("clip.wav"),
            fixture.Stage(control: 0.5),
            fixture.Stage(control: 0.5));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        INodeOutput handedOver = JointLatentOf(second).AudioLatent.Connection;
        LTXVSeparateAVLatentNode handoff = Assert.IsType<LTXVSeparateAVLatentNode>(
            handedOver?.Node);
        Assert.Same(first, handoff.AvLatent.Connection?.Node);
        // The split's audio output, not its video one — the slot is the whole contract here.
        Assert.Same(handoff.AudioLatent, handedOver);

        Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        LTXVAudioVAEDecodeNode published = PublishedAudioDecode(live);
        Assert.True(ReachesUpstream(bridge, published, upload.Id));
        Assert.True(ReachesUpstream(bridge, published, second.Id));

        live.AssertAllLive(first, second, published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Two clips merge as pixels at the batch node and as audio at the concat, each side tracing
    /// back to its own clip. The two uploads must stay distinct: collapsing them onto one loader
    /// would silently give both clips the first clip's track.
    /// </summary>
    [Fact]
    public async Task Multi_clip_merge_batches_video_and_concatenates_each_clips_own_audio()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = AudioClip(
            MediaSource.Upload,
            duration: 1.0,
            UploadedAudio("first.wav", payload: "QUFB"),
            fixture.Stage(control: 0.5));
        JObject second = AudioClip(
            MediaSource.Upload,
            duration: 1.0,
            UploadedAudio("second.wav", payload: "QkJC"),
            fixture.Stage(control: 0.5));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(first, second)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        string[] payloads = [.. bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>()
            .Select(upload => upload.AudioBase64.LiteralAsString())
            .Order(StringComparer.Ordinal)];
        Assert.Equal(["QUFB", "QkJC"], payloads);

        SwarmKSamplerNode[] stages = [StageSampler(bridge, 0), StageSampler(bridge, 1)];
        BatchImagesNodeNode batch = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        AudioConcatNode audio = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());

        ComfyNode[] batched = [.. batch.Images.Items.Select(image => image.Connection?.Node)];
        Assert.Equal(2, batched.Length);
        Assert.Equal(2, batched.Select(node => node.Id).Distinct().Count());
        Assert.All(
            batched,
            node => Assert.Single(stages, stage => ReachesUpstream(bridge, node, stage.Id)));

        ComfyNode firstAudio = audio.Audio1.Connection?.Node;
        ComfyNode secondAudio = audio.Audio2.Connection?.Node;
        Assert.True(ReachesUpstream(bridge, firstAudio, stages[0].Id));
        Assert.False(ReachesUpstream(bridge, firstAudio, stages[1].Id));
        Assert.True(ReachesUpstream(bridge, secondAudio, stages[1].Id));
        Assert.False(ReachesUpstream(bridge, secondAudio, stages[0].Id));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(batch.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(audio.Id, $"{generator.CurrentMedia.AttachedAudio.Path[0]}");

        live.AssertAllLive([.. stages, batch, audio]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Audio source is per clip: a second clip switching to <c>Upload</c> gets its own loader while
    /// the first clip stays on model-generated audio.
    /// </summary>
    [Fact]
    public async Task Switching_from_native_to_uploaded_audio_uses_only_the_second_clips_upload()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = AudioClip(
            MediaSource.Native,
            duration: 1.0,
            uploadedAudio: null,
            fixture.Stage(control: 0.5));
        JObject second = AudioClip(
            MediaSource.Upload,
            duration: 1.0,
            UploadedAudio("second.wav", payload: "QkJC"),
            fixture.Stage(control: 0.5));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Equal("QkJC", upload.AudioBase64.LiteralAsString());

        SwarmKSamplerNode nativeStage = StageSampler(bridge, 0);
        SwarmKSamplerNode uploadStage = StageSampler(bridge, 1);
        Assert.IsType<LTXVEmptyLatentAudioNode>(
            JointLatentOf(nativeStage).AudioLatent.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, JointLatentOf(uploadStage), upload.Id));

        live.AssertAllLive(nativeStage, uploadStage, upload);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The audio noise mask is a spatial mask, so it must be sized to the resolution the clip
    /// actually generates at — the document's, not the POST's. The empty video latent is the
    /// control: both must move together, or the mask happening to be right proves nothing.
    /// Text-to-video, where the document resolution is the one thing that resizes the root.
    /// </summary>
    [Fact]
    public async Task Injected_audio_mask_uses_the_documents_root_stage_resolution()
    {
        // Neither matches the POST's 512×512, so nothing here can be right by coincidence.
        const int rootWidth = 384;
        const int rootHeight = 640;
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject document = MakeDocument(AudioClip(
            MediaSource.Upload,
            duration: 1.0,
            UploadedAudio("clip.wav"),
            fixture.Stage(control: 0.5)));
        document["width"] = rootWidth;
        document["height"] = rootHeight;

        JObject workflow = await fixture.GenerateAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SetLatentNoiseMaskNode setMask = Assert.Single(
            bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        SolidMaskNode solidMask = Assert.IsType<SolidMaskNode>(setMask.Mask.Connection?.Node);
        Assert.Equal(rootWidth, solidMask.Width.LiteralAsInt());
        Assert.Equal(rootHeight, solidMask.Height.LiteralAsInt());

        EmptyLTXVLatentVideoNode emptyVideo = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(rootWidth, emptyVideo.Width.LiteralAsInt());
        Assert.Equal(rootHeight, emptyVideo.Height.LiteralAsInt());

        live.AssertAllLive(solidMask, emptyVideo, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A timeline segment over a clip with no base track conditions generation: the segment is
    /// trimmed, volume-adjusted and laid over silence, then encoded and masked so only its own
    /// seconds are preserved and the model fills the gaps. The absent <c>SetLatentNoiseMask</c> is
    /// the distinction — that is the preserve-everything mask a full base track would get.
    /// </summary>
    [Fact]
    public async Task Timeline_segment_over_no_base_track_conditions_generation_through_a_window_mask()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(AudioClip(
            MediaSource.Upload,
            duration: 10.0,
            uploadedAudio: null,
            fixture.Stage(control: 0.5)));
        document["audioTracks"] = new JArray(
            AudioTrack("track-seg", volume: 0.5, fileName: "timeline.wav", AudioSpan(
                timelineStartSeconds: 1.0,
                timelineLengthSeconds: 2.0,
                sourceStartSeconds: 0.5)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmSetAudioMaskWindowsNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        Assert.Equal((1.0, 3.0), OnlyWindowOf(mask));
        Assert.Empty(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());

        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(0.5, trim.StartIndex.LiteralAsDouble());
        Assert.Equal(2.0, trim.Duration.LiteralAsDouble());
        AudioAdjustVolumeNode volume = Assert.Single(
            bridge.Graph.NodesOfType<AudioAdjustVolumeNode>());
        // Half amplitude in decibels.
        Assert.Equal(-6, volume.Volume.LiteralAsInt());

        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Same(encode.AudioLatent, mask.Samples.Connection);
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(mask.Latent, JointLatentOf(sampler).AudioLatent.Connection);

        live.AssertAllLive(mask, trim, volume, encode, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The same segment under a text root, where the first clip's stage replaces core's chain
    /// rather than conditioning it. Conditioning the chain there reached nothing: the stage built a
    /// fresh <c>LTXVEmptyLatentAudio</c> over the top, so the clip generated unconditioned audio
    /// and shipped the entire segment chain to ComfyUI orphaned, with no warning.
    /// </summary>
    [Fact]
    public async Task A_text_root_clips_timeline_segment_conditions_the_stage_that_replaces_the_root()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject document = MakeDocument(AudioClip(
            MediaSource.Upload,
            duration: 10.0,
            uploadedAudio: null,
            fixture.Stage(control: 0.5)));
        document["audioTracks"] = new JArray(
            AudioTrack("track-seg", volume: 0.5, fileName: "timeline.wav", AudioSpan(
                timelineStartSeconds: 1.0,
                timelineLengthSeconds: 2.0,
                sourceStartSeconds: 0.5)));

        JObject workflow = await fixture.GenerateAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmSetAudioMaskWindowsNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        Assert.Equal((1.0, 3.0), OnlyWindowOf(mask));
        Assert.Same(
            mask.Latent,
            JointLatentOf(StageSampler(bridge, 0)).AudioLatent.Connection);
        // The fresh empty latent that used to displace it: there is no second audio latent at all.
        Assert.Empty(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());

        live.AssertAllLive(mask, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A clip taking its length from its own audio track builds one encode chain, not two. The
    /// second, conditioning core's chain, was pure waste under a text root — the stage replaces
    /// that chain, so ComfyUI ran a whole extra audio VAE encode for a graph nobody samples.
    /// </summary>
    [Fact]
    public async Task A_text_root_clip_sized_from_its_audio_encodes_that_audio_once()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = AudioClip(
            MediaSource.Upload,
            duration: null,
            uploadedAudio: UploadedAudio(),
            fixture.Stage(control: 0.5));
        clip["clipLengthFromAudio"] = true;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.True(
            ReachesUpstream(bridge, StageSampler(bridge, 0), encode.Id),
            "The clip's audio is encoded but never sampled.");

        live.AssertLive(encode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Timeline audio is placed by seconds, so a clip with no duration has no timeline window to
    /// project onto and nothing may condition its generation.
    /// </summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData(10.0, 1)]
    public async Task Timeline_segments_condition_generation_only_when_the_clip_has_a_duration(
        double? duration,
        int expectedMasks)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(AudioClip(
            MediaSource.Upload,
            duration,
            uploadedAudio: null,
            fixture.Stage(control: 0.5)));
        document["audioTracks"] = new JArray(
            AudioTrack("track-seg", volume: 1.0, fileName: "timeline.wav", AudioSpan(
                timelineStartSeconds: 1.0,
                timelineLengthSeconds: 2.0,
                sourceStartSeconds: 0.0)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            expectedMasks,
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>().Count);
        // Without a duration nothing of the lane is built at all, not merely left unconnected.
        Assert.Equal(
            expectedMasks,
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>().Count);

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// One timeline span crossing a clip boundary becomes two masks, each in its own clip's local
    /// seconds, and each must reach its own sampler — a span that conditioned only the clip it
    /// started in would leave the second half of the sound unaccounted for.
    /// </summary>
    [Fact]
    public async Task Timeline_segment_crossing_a_clip_boundary_conditions_both_clips()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = AudioClip(
            MediaSource.Native,
            duration: 5.0,
            uploadedAudio: null,
            fixture.Stage(control: 0.5));
        first["boundaryOut"] = Constants.BoundaryOutContinue;
        first["boundaryOutOverlap"] = 24;
        JObject second = AudioClip(
            MediaSource.Native,
            duration: 5.0,
            uploadedAudio: null,
            fixture.Stage(control: 0.5));

        JObject document = MakeDocument(first, second);
        document["audioTracks"] = new JArray(
            AudioTrack("track-seg", volume: 2.0, fileName: "timeline.wav", AudioSpan(
                timelineStartSeconds: 4.0,
                timelineLengthSeconds: 2.0,
                sourceStartSeconds: 0.0)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // One upload, decoded once and windowed twice.
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        IReadOnlyList<SwarmSetAudioMaskWindowsNode> masks =
            [.. bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>()];
        Assert.Equal(2, masks.Count);

        SwarmKSamplerNode[] stages = [StageSampler(bridge, 0), StageSampler(bridge, 1)];
        SwarmSetAudioMaskWindowsNode MaskConditioning(SwarmKSamplerNode stage) => Assert.Single(
            masks,
            mask => ReferenceEquals(JointLatentOf(stage).AudioLatent.Connection, mask.Latent));

        // Clip 0 runs 0-5s and keeps the span's first second. Clip 1's copy is rebased to its own
        // start and pushed out by the one-second continue handle it generates ahead of its content.
        Assert.Equal((4.0, 5.0), OnlyWindowOf(MaskConditioning(stages[0])));
        Assert.Equal((1.0, 2.0), OnlyWindowOf(MaskConditioning(stages[1])));

        live.AssertAllLive([.. stages, .. masks]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An enabled but empty document must not gate core: core's own image-to-video chain runs
    /// untouched, and the extension declines to plan at all.
    /// </summary>
    [Fact]
    public async Task Empty_configuration_leaves_core_generation_untouched()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Null(generator.GetVideoExecutionPlanContext());
        // Core's base pass plus its one video pass. A stage would add a third sampler; seeds cannot
        // tell them apart here, because core's video sampler also seeds Seed + 42.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.IsType<LTXVEmptyLatentAudioNode>(joint.AudioLatent.Connection?.Node);
        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(
            $"{generator.CurrentMedia.Path[0]}",
            save.Images.Connection?.Node.Id);

        live.AssertAllLive(fixture.BaseSampler(bridge), joint);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A retake windows audio and video together through the stock mask-by-time node. It rewrites
    /// five things at once — both conditionings, the joint latent and two blend coefficient sets —
    /// so its mere presence proves nothing: the sampler has to be reading its rewritten outputs.
    /// Text-to-video, so the sourced clip is the only sampler in the graph.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retake_windows_the_joint_audio_video_latent_to_the_retake_range(bool retake)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = RetakeClip(
            retake
                ? new JObject
                {
                    ["startSeconds"] = 1.0,
                    ["lengthSeconds"] = 1.0,
                    ["strength"] = 0.8,
                }
                : null,
            fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["text2videoframes"] = Ltx2WorkflowFixture.RetakeClipFrames);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        if (!retake)
        {
            Assert.Empty(bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());
            Assert.Empty(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        }
        else
        {
            // The video half keeps its own per-frame mask ladder; the by-time node adds the audio.
            Assert.Single(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
            LTXVSetAudioVideoMaskByTimeNode maskByTime = Assert.Single(
                bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());

            JObject inputs = (JObject)AsWorkflowNode(maskByTime, workflow).Node["inputs"];
            // The node must own the video mask too: its merge with a pre-existing mask only fires
            // for per-frame scalar masks, and the retake mask is H×W-resized.
            Assert.True(inputs["mask_video"].Value<bool>());
            Assert.True(inputs["mask_audio"].Value<bool>());
            Assert.Equal(0.0, inputs["mask_init_value_video"].Value<double>(), precision: 6);
            Assert.Equal(0.0, inputs["mask_init_value_audio"].Value<double>(), precision: 6);
            // Frames [24, 48) of 97 snap to latent frames [3, 6), whose boundary pixels are 17/41.
            Assert.Equal(17.0 / 24, maskByTime.StartTime.LiteralAsDouble().Value, precision: 6);
            Assert.Equal(41.0 / 24, maskByTime.EndTime.LiteralAsDouble().Value, precision: 6);

            Assert.True(
                ReachesUpstream(bridge, sampler.LatentImage.Connection?.Node, maskByTime.Id),
                "The sampler does not consume the mask-by-time node's rewritten joint latent.");
            Assert.True(
                ReachesUpstream(bridge, sampler.Positive.Connection?.Node, maskByTime.Id),
                "The sampler does not consume the mask-by-time node's rewritten conditioning.");
            live.AssertLive(maskByTime);
        }

        live.AssertAllLive(sampler);
        AssertShippable(bridge, workflow, live);
    }

    // ---- sources the request names but the workflow does not carry ----------------------

    /// <summary>
    /// An AceStepFun track is planted by a sibling extension at a reserved node id. Naming one that
    /// is not there warns by name and generates anyway, on the model's own audio.
    /// </summary>
    [Fact]
    public async Task Missing_selected_ace_audio_track_warns_and_continues_without_it()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = AudioClip("audio7", duration: null, uploadedAudio: null, fixture.Stage());

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("audio7", StringComparison.Ordinal)
                && warning.Contains("continuing without that source", StringComparison.Ordinal));

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.IsType<LTXVEmptyLatentAudioNode>(
            JointLatentOf(sampler).AudioLatent.Connection?.Node);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());

        live.AssertAllLive(sampler, PublishedAudioDecode(live));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// ControlNet audio is captured off a drive source core wired into the graph. The IC-LoRA entry
    /// is what fixes <em>which</em> source, so its weights have to resolve — an unregistered LoRA
    /// drops the entry and the fallback scan warns about a non-unique source instead. With the
    /// source pinned and nothing captured under it, the clip warns and falls back to silence.
    /// </summary>
    [Fact]
    public async Task Missing_selected_controlnet_audio_capture_warns_and_uses_silence()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_ControlNetIcLora.safetensors");
        JObject clip = AudioClip(
            MediaSource.ControlNet,
            duration: null,
            uploadedAudio: null,
            fixture.Stage());
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "UnitTest_ControlNetIcLora",
            ["driveSource"] = MediaSource.ControlNetOne,
            ["driveData"] = $"{IcLoraDriveData.Visual}",
        });

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("ControlNet 1 audio", StringComparison.Ordinal)
                && warning.Contains("using silence", StringComparison.Ordinal));

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.IsType<LTXVEmptyLatentAudioNode>(
            JointLatentOf(sampler).AudioLatent.Connection?.Node);

        live.AssertAllLive(sampler, PublishedAudioDecode(live));
        AssertShippable(bridge, workflow, live);
    }
}
