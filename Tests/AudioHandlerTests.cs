using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Execution.Audio;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioHandlerTests
{
    private static ClipPlan Clip(
        int id,
        string audioSource,
        bool saveAudioTrack,
        bool clipLengthFromControlNet = false)
    {
        StageSpec stage = new(
            Id: id,
            Control: 1,
            Upscale: 1,
            UpscaleMethod: "pixel-lanczos",
            Model: "ltx-2",
            Steps: 8,
            CfgScale: 1,
            Sampler: "euler",
            Scheduler: "normal",
            ImageReference: "Generated");
        ClipSpec clip = new(
            Id: id,
            Frames: 49,
            AudioSource: audioSource,
            IcLoras: [],
            SaveAudioTrack: saveAudioTrack,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: clipLengthFromControlNet,
            ReuseAudio: false,
            UploadedAudio: null,
            FrameRefs: [],
            Stages: [stage]);
        TimelineSpec spec = new(768, 512, 24, true, [clip]);
        return Assert.Single(TestPlanCompiler.Compile(spec).Clips);
    }

    private static WorkflowGenerator CreateGenerator(JObject workflow)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow
        };
        generator.CurrentAudioVae = new WGNodeData(
            new JArray("900", 0), generator, WGNodeData.DT_AUDIOVAE, T2IModelClassSorter.CompatLtxv2);
        return generator;
    }

    private static VideoExecutionPlan Plan(params ClipPlan[] clips) => new(
        Width: 512,
        Height: 512,
        FramesPerSecond: 24,
        new RootPlan(
            HostRootKind.ImageToVideo,
            DiscardsRoot: false,
            UsesGeneratedClipDonor: false,
            InterceptsHostCore: true,
            UsesStageHandoff: true,
            DropsTextToVideoRootDonor: false,
            DiscardsTextToVideoRoot: false),
        Clips: clips,
        Boundaries: [],
        Diagnostics: []);

    private static List<string> RequestWarnings(WorkflowGenerator generator) =>
        Assert.IsType<List<string>>(generator.UserInput.ExtraMeta["parser_warnings"]);

    private static AudioTimelineExecutor TimelineExecutor(WorkflowGenerator generator) => new(
        generator,
        new LtxAudioInjector(
            generator,
            new RootVideoStageResizer(generator)));

    /// <summary>
    /// These ids are minted by a different extension. SwarmUI-AceStepFun's
    /// <c>AudioWorkflow.GetTrackNodeId</c> feeds <c>GetStableDynamicID(64100 + branch*1000 +
    /// track*100 + 60, 0)</c>, and that helper returns <c>1000 + index</c> — so branch 0's decode
    /// lands on 65160 + track*100, which is what <see cref="AudioHandler"/> recomputes to find it.
    /// Every other test here seeds through <c>MakeAceStepFunDecodeId</c>, so a drifted base would
    /// seed the drifted value and still pass while real AceStepFun audio stopped being found.
    /// Pinning the literal once is the only thing that catches that.
    /// </summary>
    [Theory]
    [InlineData(0, "65160")]
    [InlineData(1, "65260")]
    [InlineData(7, "65860")]
    public void AceStepFun_decode_ids_match_the_sibling_extensions_published_formula(
        int trackIndex,
        string expected) =>
        Assert.Equal(expected, AudioHandler.MakeAceStepFunDecodeId(trackIndex));

    [Fact]
    public void DetectAceStepFunAudio_returns_decode_audio_for_matching_track()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));
        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(1));

        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio(1);

        Assert.True(JToken.DeepEquals(
            audio.Path,
            new JArray(AudioHandler.MakeAceStepFunDecodeId(1), 0)));
    }

    [Fact]
    public void DetectAceStepFunAudio_finds_decode_when_downstream_save_is_absent()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio(0);

        Assert.True(JToken.DeepEquals(
            audio.Path,
            new JArray(AudioHandler.MakeAceStepFunDecodeId(0), 0)));
    }

    [Fact]
    public void DetectAceStepFunAudio_returns_null_for_invalid_track()
    {
        JObject workflow = [];
        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio(-1);

        Assert.Null(audio);
    }

    [Fact]
    public void DetectAceStepFunAudio_returns_null_when_no_decode_matches_track()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio(7);

        Assert.Null(audio);
    }

    [Fact]
    public void Runtime_source_resolver_warns_when_AceStepFun_track_index_is_missing()
    {
        WorkflowGenerator generator = CreateGenerator([]);
        ClipPlan authored = Clip(0, "audio0", saveAudioTrack: false);
        ClipPlan clip = authored with
        {
            Audio = authored.Audio with
            {
                Base = authored.Audio.Base with { AceStepFunTrack = null },
            },
        };

        AudioRuntimeSources sources =
            new AudioRuntimeSourceResolver(generator, new AudioHandler(generator))
                .Resolve(Plan(clip));

        Assert.Empty(sources.ClipAudios);
        Assert.Contains(
            RequestWarnings(generator),
            warning => warning.Contains("without a valid track"));
    }

    [Fact]
    public void Runtime_source_resolver_warns_when_AceStepFun_track_is_absent()
    {
        WorkflowGenerator generator = CreateGenerator([]);
        ClipPlan clip = Clip(0, "audio7", saveAudioTrack: false);

        AudioRuntimeSources sources =
            new AudioRuntimeSourceResolver(generator, new AudioHandler(generator))
                .Resolve(Plan(clip));

        Assert.Empty(sources.ClipAudios);
        Assert.Contains(
            RequestWarnings(generator),
            warning => warning.Contains("not present in the workflow"));
    }

    [Fact]
    public void Runtime_source_resolver_warns_when_ControlNet_source_is_ambiguous()
    {
        WorkflowGenerator generator = CreateGenerator([]);
        ClipPlan clip = Clip(0, MediaSource.ControlNet, saveAudioTrack: false);

        AudioRuntimeSources sources =
            new AudioRuntimeSourceResolver(generator, new AudioHandler(generator))
                .Resolve(Plan(clip));

        Assert.Empty(sources.ClipAudios);
        Assert.Contains(
            RequestWarnings(generator),
            warning => warning.Contains("without a unique valid ControlNet 1-3 drive source"));
    }

    [Fact]
    public void Runtime_source_resolver_warns_when_planned_ControlNet_audio_is_missing()
    {
        WorkflowGenerator generator = CreateGenerator([]);
        ClipPlan authored = Clip(0, MediaSource.ControlNet, saveAudioTrack: false);
        ClipPlan clip = authored with
        {
            ArchitecturePayload = authored.RequireLtx2Payload() with
            {
                ControlNetSourceIndex = 0,
            },
        };

        AudioRuntimeSources sources =
            new AudioRuntimeSourceResolver(generator, new AudioHandler(generator))
                .Resolve(Plan(clip));

        Assert.Empty(sources.ClipAudios);
        Assert.Contains(
            RequestWarnings(generator),
            warning => warning.Contains("captured video audio is unavailable"));
    }

    [Fact]
    public void ControlNet_length_without_a_source_warns_and_keeps_authored_length()
    {
        WorkflowGenerator generator = CreateGenerator([]);
        ClipPlan authored = Clip(0, MediaSource.ControlNet, saveAudioTrack: false);
        ClipPlan clip = authored with
        {
            Audio = authored.Audio with { LengthOwner = AudioLengthOwner.ControlNet },
        };

        TimelineExecutor(generator).ApplyControlNetClipLength(clip);

        Assert.Contains(
            RequestWarnings(generator),
            warning => warning.Contains("using the authored clip length instead"));
    }

    [Fact]
    public void Stage_latent_ControlNet_length_without_a_source_keeps_authored_length()
    {
        WorkflowGenerator generator = CreateGenerator([]);
        ClipPlan clip = Clip(
            0,
            MediaSource.ControlNet,
            saveAudioTrack: false,
            clipLengthFromControlNet: true);

        Assert.Equal(AudioLengthOwner.ControlNet, clip.Audio.LengthOwner);
        Assert.Null(clip.RequireLtx2Payload().ControlNetSourceIndex);
        Assert.Null(
            new LtxStageLatentAudioFactory(generator)
                .TryResolveControlNetLengthFrames(clip));
    }

    [Fact]
    public void ControlNet_length_without_captured_frames_warns_and_keeps_authored_length()
    {
        WorkflowGenerator generator = CreateGenerator([]);
        ClipPlan authored = Clip(0, MediaSource.ControlNet, saveAudioTrack: false);
        ClipPlan clip = authored with
        {
            Audio = authored.Audio with { LengthOwner = AudioLengthOwner.ControlNet },
            ArchitecturePayload = authored.RequireLtx2Payload() with
            {
                ControlNetSourceIndex = 0,
            },
        };

        TimelineExecutor(generator).ApplyControlNetClipLength(clip);

        Assert.Contains(
            RequestWarnings(generator),
            warning => warning.Contains("captured video frame count is unavailable"));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_removes_downstream_saves_of_any_audio_save_type()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

            SaveAudioMP3Node mp3 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            mp3.AudioInput.ConnectTo(decode.AUDIO);
            bridge.AddNode(mp3, "64170");

            SaveAudioNode wav = new();
            wav.AudioInput.ConnectTo(decode.AUDIO);
            bridge.AddNode(wav, "64171");

            SaveAudioMP3Node unrelated = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_2_");
            bridge.AddNode(unrelated, "64270");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [Clip(id: 0, audioSource: "audio0", saveAudioTrack: false)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.Null(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
        Assert.Null(after.Graph.GetNode<SaveAudioNode>("64171"));
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64270"));
        Assert.NotNull(after.Graph.GetNode<VAEDecodeAudioNode>(AudioHandler.MakeAceStepFunDecodeId(0)));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_keeps_save_when_track_marked_to_save()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save1.AudioInput.ConnectTo(decode.AUDIO);
            bridge.AddNode(save1, "64170");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [Clip(id: 0, audioSource: "audio0", saveAudioTrack: true)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_keeps_save_for_a_clip_with_no_stages()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

            SaveAudioMP3Node save = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save.AudioInput.ConnectTo(decode.AUDIO);
            bridge.AddNode(save, "64170");
        }
        ClipPlan sourced = new(
            ClipId: 0,
            Frames: 25,
            EntryMode: ArchitectureEntryMode.InitVideo,
            InitVideo: null,
            Stages: [],
            Audio: new(
                new(AudioSourceKind.AceStepFun, "audio0", 0, true, null),
                AudioLengthOwner.Timeline,
                new([]),
                []),
            SavesAudioTrack: true);

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks([sourced]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_keeps_save_when_any_clip_wants_track_saved()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode0 = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));
            VAEDecodeAudioNode decode1 = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(1));

            SaveAudioMP3Node save0 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save0.AudioInput.ConnectTo(decode0.AUDIO);
            bridge.AddNode(save0, "64170");

            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_2_");
            save1.AudioInput.ConnectTo(decode1.AUDIO);
            bridge.AddNode(save1, "64270");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [
                Clip(id: 0, audioSource: "audio0", saveAudioTrack: false),
                Clip(id: 1, audioSource: "audio1", saveAudioTrack: true)
            ]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.Null(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64270"));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_no_op_when_no_acestepfun_track_referenced()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));
            SaveAudioMP3Node save = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save.AudioInput.ConnectTo(decode.AUDIO);
            bridge.AddNode(save, "64170");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [Clip(id: 0, audioSource: MediaSource.Native, saveAudioTrack: false)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
    }

}
