using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// The IC-LoRA cases the generated-workflow path cannot express: resolver and applicator
/// components driven directly against a hand-built generator, in states no POST reaches — an
/// unknown control mode, an incoming latent or video the timeline never produces. Everything
/// observable in a shipped graph — warnings included, since the API route's generator carries the
/// same <c>ExtraMeta</c> — lives in <see cref="Ltx2IcLoraContractTests"/>.
/// </summary>
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
            IcLoraDriveMediaContracts.Resolve(IcLoraDriveData.Visual),
            new(IcLoraDriveMediaKind.Image, null, null),
            new(
                IcLoraMediaSourceKind.Upload,
                Constants.IcLoraSourceUpload,
                IcLoraDriveMediaKind.Image,
                ControlNetIndex: null,
                HasInput: true),
            DimensionDownscaleFactor: 1,
            GuideStrength: 1);

        JArray result = new IcLoraControlSignalBuilder(generator).Apply(
            bridge,
            clipId: 0,
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

    [Fact]
    public void Invalid_uploaded_audio_warns_and_drops_the_reference()
    {
        WorkflowGenerator generator = RuntimeGenerator();
        using WorkflowBridge bridge = BridgeSync.For(generator);
        IcLoraPlan plan = RuntimeIcLoraPlan(
            IcLoraDriveData.Audio,
            IcLoraMediaSourceKind.Upload,
            IcLoraDriveMediaKind.Audio,
            data: "not-base64");

        WGNodeData resolved = new IcLoraAudioReferenceApplicator(generator).ResolveDriveAudio(
            bridge,
            plan,
            incomingMedia: null);

        Assert.Null(resolved);
        Assert.NotEmpty(RequestWarnings(generator.UserInput));
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
            ClipInputKind.EmptyLatent,
            HasInitVideo: false,
            InitVideo: null,
            Stages: [],
            Audio: null);
        StagePlan stage = new(
            StageId: 11,
            ClipStageIndex: 0,
            ClipStageRawIndex: 3,
            StageInputKind.EmptyLatent,
            IsPassthrough: false,
            ArchitecturePayload: null,
            new StageOutputPlan(
                IsTimelineTerminal: true,
                IntermediateOutputEligibility.NotEligible,
                PreserveConfiguredAudioTrackSave: false));

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
        IcLoraDriveMediaContracts.Resolve(driveData),
        new(kind, data, "drive.wav"),
        new(source, $"{source}", kind, ControlNetIndex: null, HasInput: true),
        DimensionDownscaleFactor: 1,
        GuideStrength: 1);

}
