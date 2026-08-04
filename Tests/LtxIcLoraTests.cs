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
/// components driven directly against a hand-built generator, plus the parse-time skip marker,
/// which produces no timeline to observe. Everything observable in a shipped graph — warnings
/// included, since the API route's generator carries the same <c>ExtraMeta</c> — moved to
/// <see cref="Ltx2IcLoraContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public sealed class LtxIcLoraTests
{
    private static WorkflowGenerator.WorkflowGenStep SeedRefinerImageStep() =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            UnknownNode refinerImage = bridge.AddStub("UnitTest_RefinerImage", "12").WithOutputs(WGNodeData.DT_IMAGE);
            g.CurrentMedia = refinerImage.GetOutput(0).ToWGMedia(g, WGNodeData.DT_IMAGE,
                width: 512, height: 512);
        }, 5.0);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static JObject MakeIcLora(
        string lora,
        string source = Constants.IcLoraSourceUpload,
        double strength = 1.0,
        double attentionStrength = 1.0,
        string controlType = Constants.IcLoraControlNone,
        string driveMediaData = null,
        string driveMediaFileName = "drive.mp4",
        IcLoraDriveData? driveData = null)
    {
        JObject entry = new()
        {
            ["lora"] = lora,
            ["driveSource"] = source,
            ["driveData"] = $"{driveData ?? (driveMediaData is null
                ? IcLoraDriveData.None
                : IcLoraDriveData.Visual)}",
            ["strength"] = strength,
            ["attentionStrength"] = attentionStrength,
            ["controlType"] = controlType,
        };
        if (driveMediaData is not null)
        {
            entry["driveMedia"] = new JObject
            {
                ["data"] = driveMediaData,
                ["fileName"] = driveMediaFileName,
            };
        }
        return entry;
    }

    private static (JObject Workflow, WorkflowBridge Bridge) Generate(
        JObject clip,
        TestModelBundle models,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> extraSteps = null)
    {
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps().Concat(extraSteps ?? []));
        return (workflow, WorkflowBridge.Create(workflow));
    }

    /// <summary>
    /// Parsing, not scoping: <c>ParseStages</c> breaks at the first skipped stage, so the clip ends
    /// up with zero stages and the timeline never activates — the entry's <c>stage: 1</c> scope is
    /// never even consulted. Left on the stub harness because the observable result is the absence
    /// of a timeline, which the API path would express as an ordinary non-VideoStages request.
    /// </summary>
    [Fact]
    public void Skip_marker_truncates_the_clips_stage_list()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD");
        entry["stage"] = 1;
        JObject skipped = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        skipped["skipped"] = true;
        JObject clip = MakeClip(skipped, MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        // One sampler, and it is core's own video root: the timeline contributed none. A skip that
        // dropped only its own stage would leave stage 1 generating a second one.
        Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
    }

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

    [Theory]
    [InlineData(null, "requires uploaded Audio Drive Media")]
    [InlineData(
        "data:image/png;base64,QUJD",
        "cannot consume Audio data from Image media")]
    public void Lipdub_invalid_drive_media_warns_and_drops_the_entry(
        string driveMediaData,
        string expectedMessage)
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: driveMediaData);
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            new JArray(clip).ToString());
        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildCoreVideoWorkflowSteps());

        Assert.NotEmpty(workflow);
        Assert.DoesNotContain(
            workflow.SelectTokens("$..class_type"),
            token => token.Value<string>().Contains("ICLoRA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(RequestWarnings(generator.UserInput), warning =>
            warning.Contains(expectedMessage, StringComparison.Ordinal));
    }
}
