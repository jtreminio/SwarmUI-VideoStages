using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;
using VideoStages.Architectures.Ltx2.Runtime.Guide;

namespace VideoStages.Tests;

/// <summary>Direct component tests for runtime states that generated workflows cannot produce.</summary>
[Collection("VideoStagesTests")]
public sealed class LtxIcLoraTests
{
    [Fact]
    public void Unknown_control_mode_leaves_drive_images_unmodified()
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Workflow = new JObject(),
        };
        using WorkflowBridge bridge = BridgeSync.For(generator);
        UnknownNode drive = bridge
            .AddStub("UnitTest_UnknownControlDrive", "201")
            .WithOutputs(WGNodeData.DT_IMAGE);
        JArray driveImages = new("201", 0);
        IcLoraPlan plan = new(
            EntryIndex: 0,
            ModelName: "adapter.safetensors",
            UsesAutoModel: false,
            Preset: "custom",
            ModelStrength: 1,
            AttentionStrength: 1,
            IcLoraControlMode.Unknown,
            new(
                IcLoraDriveData.Visual,
                IcLoraMediaSourceKind.Upload,
                IcLoraDriveMediaKind.Image,
                Upload: null,
                ControlNetIndex: null),
            DimensionDownscaleFactor: 1,
            GuideStrength: 1);

        JArray result = new IcLoraControlSignalBuilder(generator).Apply(
            bridge,
            clipId: 0,
            rawStageIndex: 0,
            plan,
            driveImages);

        Assert.True(JToken.DeepEquals(driveImages, result));
        Assert.Empty(bridge.Graph.NodesOfType<LoadMoGeModelNode>());
    }

    [Fact]
    public void Incoming_latent_audio_is_reused_without_decode_encode_or_ensure_churn()
    {
        using SwarmUiTestContext testContext = new();
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = new JObject(),
        };
        WGNodeData latentAudio;
        using (WorkflowBridge bridge = BridgeSync.For(generator))
        {
            UnknownNode source = bridge
                .AddStub("UnitTest_IncomingLatentAudio", "201")
                .WithOutputs(WGNodeData.DT_LATENT_AUDIO);
            latentAudio = source.GetOutput(0).ToWGNodeData(
                generator,
                WGNodeData.DT_LATENT_AUDIO);
        }

        JArray resolved =
            new IcLoraAudioReferenceApplicator(generator)
                .EnsureAudioReferenceLatent(latentAudio);

        using WorkflowBridge graph = WorkflowBridge.Create(generator.Workflow);
        Assert.True(JToken.DeepEquals(new JArray("201", 0), resolved));
        Assert.Empty(graph.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());
        Assert.Empty(graph.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Empty(graph.Graph.NodesOfType<SwarmEnsureAudioNode>());
    }

    [Fact]
    public void Incoming_audio_without_attached_audio_warns_and_drops_the_reference()
    {
        WorkflowGenerator generator = RuntimeGenerator();
        using WorkflowBridge bridge = BridgeSync.For(generator);
        IcLoraPlan plan = RuntimeIcLoraPlan(
            IcLoraDriveData.Audio,
            IcLoraMediaSourceKind.Incoming,
            IcLoraDriveMediaKind.Video);
        UnknownNode incoming = bridge
            .AddStub("UnitTest_IncomingVideoWithoutAudio", "201")
            .WithOutputs(WGNodeData.DT_VIDEO);

        WGNodeData resolved = new IcLoraAudioReferenceApplicator(generator).ResolveDriveAudio(
            bridge,
            plan,
            incoming.GetOutput(0).ToWGNodeData(generator, WGNodeData.DT_VIDEO));

        Assert.Null(resolved);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("has no attached audio"));
    }

    [Fact]
    public void Audio_reference_without_an_audio_vae_warns_and_drops_the_reference()
    {
        WorkflowGenerator generator = RuntimeGenerator();
        WGNodeData audio;
        using (WorkflowBridge bridge = BridgeSync.For(generator))
        {
            UnknownNode source = bridge
                .AddStub("UnitTest_AudioWithoutVae", "201")
                .WithOutputs(WGNodeData.DT_AUDIO);
            audio = source.GetOutput(0).ToWGNodeData(generator, WGNodeData.DT_AUDIO);
        }

        JArray resolved =
            new IcLoraAudioReferenceApplicator(generator).EnsureAudioReferenceLatent(audio);

        Assert.Null(resolved);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("none is available for the selected model"));
    }

    /// <summary>
    /// Invalid drive audio is a blocking preflight error, and the runtime path it guards treats
    /// reaching it with the same payload as a bug rather than dropping the reference.
    /// </summary>
    [Fact]
    public void Invalid_uploaded_audio_blocks_the_request_at_preflight()
    {
        WorkflowGenerator generator = RuntimeGenerator();
        using WorkflowBridge bridge = BridgeSync.For(generator);
        IcLoraPlan plan = RuntimeIcLoraPlan(
            IcLoraDriveData.Audio,
            IcLoraMediaSourceKind.Upload,
            IcLoraDriveMediaKind.Audio,
            data: "not-base64");

        PlanDiagnostic blocking = new UploadedMediaPreflight(generator.UserInput).AudioDiagnostic(
                plan.Drive.Upload.Data, plan.Drive.Upload.FileName, clipId: 0);

        Assert.NotNull(blocking);
        Assert.Equal(PlanDiagnosticSeverity.Error, blocking.Severity);
        Assert.Contains("not readable", blocking.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            new IcLoraAudioReferenceApplicator(generator).ResolveDriveAudio(
                bridge,
                plan,
                incomingMedia: null));
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
    }

    [Fact]
    public void Missing_incoming_visual_media_warns_and_drops_the_guide()
    {
        WorkflowGenerator generator = RuntimeGenerator();
        using WorkflowBridge bridge = BridgeSync.For(generator);
        IcLoraPlan plan = RuntimeIcLoraPlan(
            IcLoraDriveData.Visual,
            IcLoraMediaSourceKind.Incoming,
            IcLoraDriveMediaKind.Video);
        ClipPlan clip = new(
            ClipId: 7,
            Frames: 25,
            ArchitectureEntryMode.TextToVideo,
            InitVideo: null,
            Stages: [],
            Audio: null,
            SavesAudioTrack: false);
        StagePlan stage = new(
            StageId: 11,
            ClipStageIndex: 0,
            ClipStageRawIndex: 3,
            StageInputKind.EmptyLatent,
            IsPassthrough: false,
            ArchitecturePayload: null,
            IsIntermediateStage: false);

        bool resolved = new IcLoraVisualGuideResolver(generator).TryResolve(
            bridge,
            clip,
            stage,
            plan,
            stageInput: null,
            out ResolvedIcLoraDrive drive);

        Assert.False(resolved);
        Assert.Null(drive);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("Incoming visual media is unavailable"));
    }

    private static WorkflowGenerator RuntimeGenerator() => new()
    {
        UserInput = new T2IParamInput(null),
        Features = [],
        ModelFolderFormat = "/",
        Workflow = new JObject(),
    };

    private static IcLoraPlan RuntimeIcLoraPlan(
        IcLoraDriveData driveData,
        IcLoraMediaSourceKind source,
        IcLoraDriveMediaKind kind,
        string data = null) => new(
        EntryIndex: 0,
        ModelName: "adapter.safetensors",
        UsesAutoModel: false,
        Preset: "custom",
        ModelStrength: 1,
        AttentionStrength: 1,
        IcLoraControlMode.None,
        new(
            driveData,
            source,
            kind,
            data is null ? null : new UploadedMediaSpec(data, "drive.wav"),
            ControlNetIndex: null),
        DimensionDownscaleFactor: 1,
        GuideStrength: 1);

}
