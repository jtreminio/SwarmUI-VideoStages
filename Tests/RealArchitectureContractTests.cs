using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.MiniMax;
using VideoStages.Architectures.Wan;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Shared executable contracts for the real architecture modules. Family-specific setup ends at
/// <see cref="FamilyFixture"/>; assertions below describe only the common architecture boundary.
/// </summary>
[Collection("VideoStagesTests")]
public class RealArchitectureContractTests
{
    // At 24 fps, 0.6 seconds is 16 inclusive structural source frames. A resolved LTX or WAN
    // generated stage raises that request to 17 on its own grid.
    private const double CrossFamilyClipDuration = 0.6;
    private const int CrossFamilySourceFrames = 16;
    private const int CrossFamilyGeneratedFrames = 17;

    private static readonly string[] InitVideoClipFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    private sealed record FamilyFixture(
        IVideoArchitectureModule Module,
        T2IModel BaseModel,
        T2IModel Model,
        ModelProfileId ModelProfileId,
        string CloseImpostorClassId)
    {
        internal VideoArchitectureDescriptor Descriptor => Module.Descriptor;

        internal ClipSpec MinimalClip() =>
            new(
                0,
                25,
                Constants.AudioSourceNative,
                [],
                false,
                false,
                false,
                false,
                null,
                [],
                [
                    new(
                        10,
                        1,
                        1,
                        "pixel-lanczos",
                        Model.Name,
                        12,
                        4.5,
                        "euler",
                        "normal",
                        "Generated"),
                ]);
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("minimax")]
    [InlineData("wan22")]
    [InlineData("wan22-5b")]
    public void Model_recognition_is_exact_and_returns_the_declared_identity(string family)
    {
        using SwarmUiTestContext context = new();
        FamilyFixture fixture = CreateFixture(family);
        VideoArchitectureRegistry registry = RealRegistry();

        Assert.True(registry.TryResolveModel(
            fixture.Model,
            out ResolvedVideoModel resolved));
        Assert.Equal(fixture.Descriptor.Id, resolved.ArchitectureId);
        Assert.Equal(fixture.ModelProfileId, resolved.ModelProfileId);
        Assert.Same(fixture.Descriptor, resolved.Architecture);
        Assert.Equal(fixture.Model.Name, resolved.ModelName);

        fixture.Model.ModelClass = fixture.Model.ModelClass with
        {
            ID = fixture.CloseImpostorClassId,
        };
        bool closeVariantSupported = family == "wan22";
        Assert.Equal(
            closeVariantSupported,
            registry.TryResolveModel(
                fixture.Model,
                out ResolvedVideoModel impostorResolution));
        if (closeVariantSupported)
        {
            Assert.Equal(
                WanArchitectureModule.OrdinaryImageToVideoProfileId,
                impostorResolution.ModelProfileId);
        }
        else
        {
            Assert.Null(impostorResolution);
        }
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("minimax")]
    [InlineData("wan22")]
    [InlineData("wan22-5b")]
    public void Descriptor_publishes_the_common_executable_contract(string family)
    {
        using SwarmUiTestContext context = new();
        VideoArchitectureDescriptor descriptor = CreateFixture(family).Descriptor;

        Assert.NotEmpty(descriptor.EntryModes);
        Assert.All(descriptor.EntryModes, mode => Assert.True(Enum.IsDefined(mode)));
        Assert.True(descriptor.FrameGrid > 0);
        Assert.All(
            Enum.GetValues<BoundaryJoinType>(),
            mode => Assert.True(descriptor.BoundaryRules.ContainsKey(mode)));
        Assert.Contains(
            descriptor.EntryModes,
            mode => mode != ArchitectureEntryMode.InitVideo);
    }

    [Fact]
    public void Registry_accepts_all_real_architecture_modules()
    {
        VideoArchitectureRegistry registry = RealRegistry();

        Assert.Same(
            Ltx2ArchitectureModule.Instance,
            registry.GetModule(Ltx2ArchitectureModule.ArchitectureId));
        Assert.Same(
            MiniMaxArchitectureModule.Instance,
            registry.GetModule(MiniMaxArchitectureModule.ArchitectureId));
        Assert.Same(
            WanArchitectureModule.Instance,
            registry.GetModule(WanArchitectureModule.ArchitectureId));
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("minimax")]
    [InlineData("wan22")]
    [InlineData("wan22-5b")]
    public void Minimal_generated_image_to_video_clip_compiles_the_shared_payload_contract(
        string family)
    {
        using SwarmUiTestContext context = new();
        FamilyFixture fixture = CreateFixture(family);
        ClipSpec clip = fixture.MinimalClip();
        VideoStagesSpec spec = new(512, 512, 24, false, [clip]);
        ArchitecturePlanningResult planning =
            ArchitecturePlanResolver.Resolve(spec, RealRegistry());
        ClipArchitectureAssignment assignment = planning.Clips[clip.Id];
        ArchitectureClipCompilation architectureCompilation =
            fixture.Module.ValidateAndCompileClip(
                clip,
                assignment.StageModels,
                new(512, 512, 24, ArchitectureEntryMode.ImageToVideo));
        KeyValuePair<int, IArchitectureStagePayload> compiledEntry =
            Assert.Single(architectureCompilation.StagePayloads);
        Assert.Equal(0, compiledEntry.Key);
        Assert.NotNull(architectureCompilation.Payload);
        Assert.Equal(
            fixture.Descriptor.Id,
            architectureCompilation.Payload.ArchitectureId);
        ClipPlan directlyCompiledClip = ClipPlanCompiler.Compile(
            clip,
            new(
                IsTextToVideo: false,
                Width: 512,
                Height: 512,
                FramesPerSecond: 24,
                IsLastClip: true,
                IsMultiClip: false,
                TotalStageCount: 1,
                FirstStageOrdinal: 0,
                EntryMode: ArchitectureEntryMode.ImageToVideo,
                Architecture: assignment,
                ArchitectureCompilation: architectureCompilation));
        Assert.Same(
            compiledEntry.Value,
            Assert.Single(directlyCompiledClip.Stages).ArchitecturePayload);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            planning);

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiledClip = Assert.Single(plan.Clips);
        StagePlan compiledStage = Assert.Single(compiledClip.Stages);
        Assert.Same(fixture.Descriptor, compiledClip.Architecture);
        Assert.NotNull(compiledClip.ArchitecturePayload);
        Assert.NotNull(compiledStage.ArchitecturePayload);
        Assert.Equal(compiledClip.Architecture.Id, compiledClip.ArchitecturePayload.ArchitectureId);
        Assert.Equal(compiledClip.Architecture.Id, compiledStage.ArchitecturePayload.ArchitectureId);
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public void Source_only_clip_can_lead_a_real_generated_clip_through_a_neutral_cut(
        string generatedFamily)
    {
        using SwarmUiTestContext context = new();
        FamilyFixture fixture = CreateFixture(generatedFamily);
        JObject initVideoClip = MakeClip();
        initVideoClip["duration"] = CrossFamilyClipDuration;
        initVideoClip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };
        JObject generated = MakeClip(
            MakeStage(fixture.Model.Name, "Generated", steps: 9));
        generated["duration"] = CrossFamilyClipDuration;
        T2IParamInput input = BuildNativeInput(
            fixture.BaseModel,
            fixture.Model,
            MakeDocument(initVideoClip, generated).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                MixedArchitectureSteps(),
                features: InitVideoClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        SwarmFrameWindowNode sourceWindow = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        string[] generatedSignatureIds = generatedFamily == "wan22"
            ? [Assert.Single(NodesOfClass(bridge, "WanImageToVideo")).Id]
            : [.. bridge.Graph.NodesOfType<LTXVConditioningNode>().Select(node => node.Id)];
        Assert.NotEmpty(generatedSignatureIds);
        BatchImagesNodeNode cut = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        ComfyNode[] cutInputs = [
            .. Enumerable.Range(0, cut.Images.Count)
                .Select(index => cut.Images[index].Connection?.Node)
        ];
        Assert.Equal(2, cutInputs.Length);
        Assert.True(ReachesUpstream(bridge, cutInputs[0], sourceWindow.Id));
        Assert.False(ReachesUpstream(bridge, cutInputs[1], sourceWindow.Id));
        Assert.DoesNotContain(
            generatedSignatureIds,
            signatureId => ReachesUpstream(bridge, cutInputs[0], signatureId));
        Assert.Contains(
            generatedSignatureIds,
            signatureId => ReachesUpstream(bridge, cutInputs[1], signatureId));

        Assert.Equal(cut.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(
            CrossFamilySourceFrames + CrossFamilyGeneratedFrames,
            generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio.DataType);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path));
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public void Mixed_real_architectures_hard_cut_through_one_neutral_decoded_join(
        string firstFamily)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel first = firstFamily == "wan22"
            ? models.WanVideoModel
            : models.LtxVideoModel;
        T2IModel second = firstFamily == "wan22"
            ? models.LtxVideoModel
            : models.WanVideoModel;
        string secondFamily = firstFamily == "wan22" ? "ltx2" : "wan22";
        JObject document = MakeDocument(
            MakeClip(MakeStage(first.Name, "Generated", steps: 7)),
            MakeClip(MakeStage(second.Name, "Generated", steps: 9)));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            first,
            document.ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MixedArchitectureSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        string[] ltxSignatureIds = [
            .. bridge.Graph.NodesOfType<LTXVConditioningNode>().Select(node => node.Id)
        ];
        Assert.NotEmpty(ltxSignatureIds);
        string wanSignatureId = Assert.Single(
            NodesOfClass(bridge, "WanImageToVideo")).Id;
        BatchImagesNodeNode cut = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        ComfyNode[] cutInputs = [
            .. Enumerable.Range(0, cut.Images.Count)
                .Select(index => cut.Images[index].Connection?.Node)
        ];
        Assert.Equal(2, cutInputs.Length);
        AssertInputComesFromFamily(
            bridge,
            cutInputs[0],
            firstFamily,
            ltxSignatureIds,
            wanSignatureId);
        AssertInputComesFromFamily(
            bridge,
            cutInputs[1],
            secondFamily,
            ltxSignatureIds,
            wanSignatureId);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(cut.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(29, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        Assert.Equal(
            WGNodeData.DT_AUDIO,
            generator.CurrentMedia.AttachedAudio.DataType);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path));
    }

    private static FamilyFixture CreateFixture(string family) => family switch
    {
        "ltx2" => CreateLtxFixture(),
        "minimax" => CreateMiniMaxFixture(),
        "wan22" => CreateWanFixture(),
        "wan22-5b" => CreateWan5bFixture(),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    private static FamilyFixture CreateLtxFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        return new(
            Ltx2ArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            new("ltx-2.3"),
            "lightricks-ltx-video-2");
    }

    private static FamilyFixture CreateWanFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        return new(
            WanArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            WanArchitectureModule.ImageToVideoProfileId,
            "wan-2_1-image2video-14b");
    }

    private static FamilyFixture CreateMiniMaxFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        return new(
            MiniMaxArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            MiniMaxArchitectureModule.ProfileId,
            "minimax-h3-refiner");
    }

    private static FamilyFixture CreateWan5bFixture()
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        return new(
            WanArchitectureModule.Instance,
            models.BaseModel,
            models.VideoModel,
            WanArchitectureModule.Ti2v5bProfileId,
            $"{WanArchitectureModule.Ti2v5bModelClassId}/lora");
    }

    private static VideoArchitectureRegistry RealRegistry() =>
        new([
            Ltx2ArchitectureModule.Instance,
            MiniMaxArchitectureModule.Instance,
            WanArchitectureModule.Instance,
        ]);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> MixedArchitectureSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static void AssertInputComesFromFamily(
        WorkflowBridge bridge,
        ComfyNode input,
        string expectedFamily,
        IReadOnlyList<string> ltxSignatureIds,
        string wanSignatureId)
    {
        bool reachesLtx = ltxSignatureIds.Any(
            signatureId => ReachesUpstream(bridge, input, signatureId));
        bool reachesWan = ReachesUpstream(bridge, input, wanSignatureId);

        Assert.Equal(expectedFamily == "ltx2", reachesLtx);
        Assert.Equal(expectedFamily == "wan22", reachesWan);
    }

    private static IEnumerable<ComfyNode> NodesOfClass(
        WorkflowBridge bridge,
        string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);
}
