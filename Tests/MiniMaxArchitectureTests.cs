using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.MiniMax;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxArchitectureTests
{
    [Fact]
    public void The_checkpoint_resolves_to_MiniMax_rather_than_the_host_video_fallback()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        // The fallback also claims this compat class, so the specialized tier is what keeps H3
        // off the video-only stock path.
        Assert.True(HostVideoArchitectureModule.Instance.TryResolveModel(
            models.VideoModel,
            out _));
        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved));
        Assert.Equal(MiniMaxArchitectureModule.ArchitectureId, resolved.ArchitectureId);
        Assert.Equal(MiniMaxArchitectureModule.ProfileId, resolved.ModelProfileId);
        Assert.Equal(["first", "last"], resolved.ReferencePositions);
        Assert.Equal(MiniMaxArchitectureModule.FrameGrid, resolved.FrameGrid);
        Assert.Equal(MiniMaxArchitectureModule.FrameGridOrigin, resolved.FrameGridOrigin);
    }

    [Theory]
    [InlineData("minimax-h3/vae")]
    [InlineData("minimax-h3/audio-vae")]
    public void Support_model_classes_are_not_generation_models(string modelClassId)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
        };

        Assert.False(MiniMaxArchitectureModule.Instance.TryResolveModel(
            models.VideoModel,
            out _));
    }

    [Fact]
    public void The_published_catalog_states_the_frame_grid_audio_and_boundary_rules()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        JObject catalog = ArchitectureCatalogSerializer.Serialize(
            VideoArchitectureRegistry.Production);
        JObject architecture = Assert.Single(
            ((JArray)catalog["architectures"]).Values<JObject>(),
            item => item["id"]?.ToString() == "minimax");
        JObject model = Assert.Single(
            ((JArray)catalog["models"]).Values<JObject>(),
            item => item["modelName"]?.ToString() == models.VideoModel.Name);

        Assert.Equal("MiniMax H3", architecture.Value<string>("label"));
        Assert.Equal(
            MiniMaxArchitectureModule.FrameGrid,
            model.Value<int>("frameGrid"));
        Assert.Equal(
            MiniMaxArchitectureModule.FrameGridOrigin,
            model.Value<int>("frameGridOrigin"));
        Assert.Equal(
            ["Native", "Upload", "AceStepFun"],
            architecture["capabilities"]["audioSourceKinds"].Values<string>());
        Assert.Equal(
            ["text-to-video", "image-to-video"],
            architecture["capabilities"]["entryModes"].Values<string>());
        Assert.Equal(
            ["frameReferences"],
            architecture["capabilities"]["features"].Values<string>());
        Assert.Equal(
            "supported",
            architecture["boundaryRules"]["cut"].Value<string>("support"));
        Assert.Equal(
            "unsupported",
            architecture["boundaryRules"]["continue"].Value<string>("support"));
        Assert.Equal(
            "unsupported",
            architecture["boundaryRules"]["crossfade"].Value<string>("support"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void A_second_stage_compiles_at_every_usable_control(double control)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        Assert.Empty(CompileDiagnostics(
            models,
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: control,
                steps: 8,
                cfgScale: 1)));
    }

    [Fact]
    public void A_partial_control_that_rounds_to_start_step_zero_is_refused()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        PlanDiagnostic refusal = Assert.Single(CompileDiagnostics(
            models,
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.99,
                steps: 8,
                cfgScale: 1)));
        Assert.Equal(PlanDiagnosticSeverity.Error, refusal.Severity);
        Assert.Equal("minimax.stage-control.quantized-zero", refusal.Code);
    }

    [Fact]
    public void A_guided_cfg_warns_without_blocking_the_clip()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();

        PlanDiagnostic warning = Assert.Single(CompileDiagnostics(
            models,
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 7)));
        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("minimax.stage.cfg-scale.non-unity", warning.Code);
    }

    [Theory]
    [InlineData("cut", false)]
    [InlineData("continue", true)]
    public void Carry_audio_is_only_refused_where_a_join_could_have_used_it(
        string boundaryOut,
        bool expectWarning)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["boundaryOut"] = boundaryOut;
        clip["boundaryOutCarryAudio"] = true;
        ClipSpec parsed = ParseClip(clip, models);

        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved));
        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                parsed,
                resolved.Architecture,
                ArchitectureEntryMode.ImageToVideo);

        Assert.Equal(
            expectWarning,
            diagnostics.Any(diagnostic => diagnostic.Code
                == "effective-request.unsupported-audio-boundary-ignored"));
    }

    [Fact]
    public void A_single_clip_does_not_warn_about_inert_saved_audio_boundary_carry()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["boundaryOut"] = Constants.BoundaryOutContinue;
        clip["boundaryOutCarryAudio"] = true;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        VideoStagesSpec spec = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        ArchitecturePlanningResult architecturePlanning =
            ArchitecturePlanResolver.Resolve(spec, VideoArchitectureRegistry.Production);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architecturePlanning);

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-audio-boundary-ignored");
    }

    [Fact]
    public void Bounded_first_and_last_frame_references_compile_and_others_are_dropped()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        ClipSpec clip = ParseClip(
            MakeClipWithRefs(
                [
                    UploadedReference(fromEnd: false, frame: 1),
                    UploadedReference(fromEnd: true, frame: 1),
                    UploadedReference(fromEnd: false, frame: 9),
                ],
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1)),
            models);

        ArchitectureClipCompilation compilation = Compile(clip, models);
        MiniMaxClipPayload payload =
            Assert.IsType<MiniMaxClipPayload>(compilation.Payload);

        Assert.NotNull(payload.FirstFrameReference);
        Assert.NotNull(payload.LastFrameReference);
        Assert.Single(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.minimax-middle-frame-reference-ignored");
    }

    private static JObject UploadedReference(bool fromEnd, int frame) =>
        new()
        {
            ["source"] = "Upload",
            ["frame"] = frame,
            ["fromEnd"] = fromEnd,
            ["uploadedImage"] = new JObject
            {
                ["data"] = "data:image/png;base64,AAAA",
                ["fileName"] = fromEnd ? "last.png" : "first.png",
            },
        };

    private static IReadOnlyList<PlanDiagnostic> CompileDiagnostics(
        TestModelBundle models,
        params JObject[] stages) =>
        Compile(ParseClip(MakeClip(stages), models), models).Diagnostics;

    private static ArchitectureClipCompilation Compile(
        ClipSpec clip,
        TestModelBundle models)
    {
        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved));
        Dictionary<int, ResolvedVideoModel> stageModels = (clip.Stages ?? [])
            .ToDictionary(stage => stage.ClipStageRawIndex, _ => resolved);
        return MiniMaxArchitectureModule.Instance.ValidateAndCompileClip(
            clip,
            stageModels,
            new(1344, 768, 24));
    }

    private static ClipSpec ParseClip(JObject clip, TestModelBundle models)
    {
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        VideoStagesSpec spec = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        return Assert.Single(spec.Clips);
    }
}
