using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class EffectiveVideoRequestTests
{
    [Fact]
    public void Projection_preserves_authored_values_and_removes_every_ignored_Wan_value()
    {
        StageSpec first = Stage(0, rawIndex: 0);
        StageSpec second = Stage(
            1,
            rawIndex: 1,
            upscale: 2,
            upscaleMethod: "latentmodel-detail.safetensors") with
        {
            IcLoraStrengths = [0.8],
        };
        ClipSpec clip = Clip(first, second) with
        {
            AuthoredArchitectureId = "stale-architecture",
            AuthoredModelProfileId = "stale-profile",
            AuthoredStages =
            [
                new(0, first.Model, "old-first-profile", false),
                new(1, second.Model, "old-second-profile", false),
            ],
            IcLoras =
            [
                new(
                    "wan-ic.safetensors",
                    Constants.IcLoraSourceUpload,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null),
            ],
        };
        VideoStagesSpec authored = Spec(clip);

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            ResolveWan(authored));

        ClipSpec effective = Assert.Single(request.Spec.Clips);
        Assert.Equal("stale-architecture", clip.AuthoredArchitectureId);
        Assert.Equal("stale-profile", clip.AuthoredModelProfileId);
        Assert.Single(clip.IcLoras);
        Assert.Equal(2, clip.Stages[1].Upscale);
        Assert.Equal([0.8], clip.Stages[1].IcLoraStrengths);

        Assert.Equal(WanArchitectureModule.ArchitectureId.Value, effective.AuthoredArchitectureId);
        Assert.Equal(
            WanArchitectureModule.ImageToVideoProfileId.Value,
            effective.AuthoredModelProfileId);
        Assert.All(
            effective.AuthoredStages,
            stage => Assert.Equal(
                WanArchitectureModule.ImageToVideoProfileId.Value,
                stage.ModelProfileId));
        Assert.Empty(effective.IcLoras);
        Assert.All(effective.Stages, stage => Assert.Empty(stage.IcLoraStrengths));
        Assert.Equal(1, effective.Stages[1].Upscale);
        Assert.Equal("pixel-lanczos", effective.Stages[1].UpscaleMethod);
        Assert.All(
            request.Decisions.Where(decision =>
                decision.Code.Contains("stale", StringComparison.Ordinal)
                || decision.Code.Contains("ignored", StringComparison.Ordinal)),
            decision => Assert.Equal(
                EffectiveRequestDisposition.IgnoreWithWarning,
                decision.Disposition));
    }

    [Fact]
    public void Compilation_cannot_leak_ignored_Wan_values_back_into_payloads()
    {
        StageSpec first = Stage(0, rawIndex: 0);
        StageSpec second = Stage(
            1,
            rawIndex: 1,
            upscale: 2,
            upscaleMethod: "model-detail.safetensors") with
        {
            IcLoraStrengths = [1],
        };
        ClipSpec clip = Clip(first, second) with
        {
            IcLoras =
            [
                new(
                    "wan-ic.safetensors",
                    Constants.IcLoraSourceUpload,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null),
            ],
        };
        VideoStagesSpec spec = Spec(clip);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "effective-request.wan-ic-lora-ignored");
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-advanced-upscale-ignored");
        WanStagePayload payload = plan.Clips[0].Stages[1].RequireWanPayload();
        Assert.Equal(StageUpscaleMode.None, payload.Upscale.Mode);
        Assert.Equal(1, payload.Upscale.Factor);
    }

    [Fact]
    public void Generic_projection_warns_for_stage_only_reference_payload()
    {
        StageSpec stage = Stage(
            0,
            rawIndex: 0,
            model: "host-model") with
        {
            ImageRefStrengths = [0.7],
        };
        ClipSpec clip = Clip(stage) with
        {
            AuthoredArchitectureId =
                HostVideoArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileId =
                HostVideoArchitectureModule.ProfileId.Value,
            AuthoredStages =
            [
                new(
                    0,
                    stage.Model,
                    HostVideoArchitectureModule.ProfileId.Value,
                    false),
            ],
        };
        VideoStagesSpec authored = Spec(clip);
        ArchitecturePlanningResult architectures = Resolve(
            authored,
            _ => HostVideoArchitectureModule.Instance,
            _ => HostVideoArchitectureModule.Instance.Descriptor);

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(authored, architectures);

        Assert.Equal([0.7], authored.Clips[0].Stages[0].ImageRefStrengths);
        Assert.Empty(request.Spec.Clips[0].Stages[0].ImageRefStrengths);
        Assert.Contains(
            request.Decisions,
            decision => decision.Code
                    == "effective-request.host-video-references-ignored"
                && decision.Disposition
                    == EffectiveRequestDisposition.IgnoreWithWarning);
    }

    [Fact]
    public void Unsupported_non_cut_boundary_uses_an_effective_cut_without_editing_authored_data()
    {
        ClipSpec ltxClip = LtxClip() with
        {
            BoundaryOut = Constants.BoundaryOutCrossfade,
            BoundaryOutOverlap = 16,
            BoundaryOutCarryAudio = true,
        };
        ClipSpec wanClip = Clip(Stage(0, rawIndex: 0)) with { Id = 1 };
        VideoStagesSpec authored = Spec(ltxClip, wanClip);
        ArchitecturePlanningResult architectures = ResolveMixed(authored);

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(authored, architectures);
        ClipSpec effectiveLeft = request.Spec.Clips[0];
        Assert.Equal(Constants.BoundaryOutCrossfade, ltxClip.BoundaryOut);
        Assert.Equal(16, ltxClip.BoundaryOutOverlap);
        Assert.True(ltxClip.BoundaryOutCarryAudio);
        Assert.Equal(Constants.BoundaryOutCut, effectiveLeft.BoundaryOut);
        Assert.Equal(0, effectiveLeft.BoundaryOutOverlap);
        Assert.False(effectiveLeft.BoundaryOutCarryAudio);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            authored,
            RootEnvironment.FromSpec(authored),
            architectures);
        BoundaryPlan boundary = Assert.Single(plan.Boundaries);
        Assert.Equal(BoundaryExecutionMode.Crossfade, boundary.Requested);
        Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        Assert.Equal(
            BoundaryFallback.ArchitectureRuleUnsupported,
            boundary.Fallback);
        Assert.False(boundary.CarryAudio);
        Assert.Single(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                    == "effective-request.boundary-degraded-to-cut"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "boundary-cross-architecture-non-cut"
                || diagnostic.Code == "boundary-architectureruleunsupported");
    }

    [Fact]
    public void Unknown_Wan_upscale_remains_blocking_and_is_not_sanitized()
    {
        StageSpec first = Stage(0, rawIndex: 0);
        StageSpec unknown = Stage(
            1,
            rawIndex: 1,
            upscale: 2,
            upscaleMethod: "future-upscale");
        VideoStagesSpec spec = Spec(Clip(first, unknown));
        RecordingWanModule module = new();
        ArchitecturePlanningResult architectures = ResolveWan(spec, module);

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(spec, architectures);

        Assert.Equal(2, request.Spec.Clips[0].Stages[1].Upscale);
        Assert.Contains(
            request.Decisions,
            decision => decision.Code == "effective-request.unknown-upscale"
                && decision.Disposition == EffectiveRequestDisposition.Block);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architectures);
        PlanDiagnostic error = Assert.Single(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal("effective-request.unknown-upscale", error.Code);
        Assert.Equal(0, module.CompileCount);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported");
        Assert.Null(plan.Clips[0].ArchitecturePayload);
    }

    [Fact]
    public void Manual_posted_Wan_document_continues_and_publishes_ignore_warnings()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        first["modelProfileId"] = "old-first-profile";
        first["icLoraStrengths"] = new JArray(0.6);
        JObject second = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0,
            upscale: 2,
            upscaleMethod: "latent-detail",
            steps: 10);
        second["modelProfileId"] = "old-second-profile";
        second["icLoraStrengths"] = new JArray(0.8);
        JObject clip = MakeClip(first, second);
        clip["architecture"] = "old-wan-cache";
        clip["modelProfileId"] = "old-wan-profile";
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "wan-ic.safetensors",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["strength"] = 1,
            ["attentionStrength"] = 1,
            ["controlType"] = Constants.IcLoraControlNone,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/",
        };

        VideoExecutionPlan plan = Assert.IsType<VideoExecutionPlanContext>(
            generator.GetVideoExecutionPlanContext()).Plan;
        generator.RequireVideoExecutionPlanContext();

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Contains(warnings, warning => warning.Contains("cached architecture"));
        Assert.Contains(warnings, warning => warning.Contains("cached model profile"));
        Assert.Contains(warnings, warning => warning.Contains("WAN does not support IC-LoRA"));
        Assert.Contains(warnings, warning => warning.Contains("runs the stage at 1×"));
        ClipSpec authored = Assert.Single(generator.GetVideoStagesSpec().Clips);
        Assert.Equal("old-wan-cache", authored.AuthoredArchitectureId);
        Assert.Equal("old-wan-profile", authored.AuthoredModelProfileId);
        Assert.Single(authored.IcLoras);
        Assert.Equal(2, authored.Stages[1].Upscale);
        Assert.Equal([0.8], authored.Stages[1].IcLoraStrengths);
        Assert.Equal(
            StageUpscaleMode.None,
            plan.Clips[0].Stages[1].RequireWanPayload().Upscale.Mode);
    }

    private static StageSpec Stage(
        int id,
        int rawIndex,
        double upscale = 1,
        string upscaleMethod = "pixel-lanczos",
        string model = "wan-model") =>
        new(
            id,
            Control: id == 0 ? 1 : 0.5,
            Upscale: upscale,
            UpscaleMethod: upscaleMethod,
            Model: model,
            Steps: 10,
            CfgScale: 4,
            Sampler: "euler",
            Scheduler: "normal",
            ImageReference: id == 0 ? "Generated" : "PreviousStage",
            ClipStageIndex: id,
            ClipStageRawIndex: rawIndex,
            IcLoraStrengths: []);

    private static ClipSpec Clip(params StageSpec[] stages) =>
        new(
            Id: 0,
            Frames: 25,
            AudioSource: Constants.AudioSourceNative,
            IcLoras: [],
            SaveAudioTrack: false,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: false,
            UploadedAudio: null,
            ImageRefs: [],
            Stages: stages)
        {
            AuthoredArchitectureId = WanArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileId = WanArchitectureModule.ImageToVideoProfileId.Value,
            AuthoredStages = stages
                .Select(stage => new AuthoredStageModelSpec(
                    stage.ClipStageRawIndex,
                    stage.Model,
                    WanArchitectureModule.ImageToVideoProfileId.Value,
                    false))
                .ToArray(),
        };

    private static ClipSpec LtxClip()
    {
        StageSpec stage = Stage(
            0,
            rawIndex: 0,
            model: "ltx-model");
        return Clip(stage) with
        {
            AuthoredArchitectureId = Ltx2ArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileId = "ltx-2.3",
            AuthoredStages = [new(0, stage.Model, "ltx-2.3", false)],
        };
    }

    private static VideoStagesSpec Spec(params ClipSpec[] clips) =>
        new(512, 512, 24, false, clips);

    private static ArchitecturePlanningResult ResolveWan(
        VideoStagesSpec spec,
        IVideoArchitectureModule module = null)
    {
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor;
        return Resolve(
            spec,
            _ => module ?? WanArchitectureModule.Instance,
            _ => descriptor);
    }

    private static ArchitecturePlanningResult ResolveMixed(VideoStagesSpec spec) =>
        Resolve(
            spec,
            clip => clip.Id == 0
                ? Ltx2ArchitectureModule.Instance
                : WanArchitectureModule.Instance,
            clip => clip.Id == 0
                ? Ltx2ArchitectureModule.Instance.Descriptor
                : WanArchitectureModule.Instance.Descriptor);

    private static ArchitecturePlanningResult Resolve(
        VideoStagesSpec spec,
        Func<ClipSpec, IVideoArchitectureModule> moduleFor,
        Func<ClipSpec, VideoArchitectureDescriptor> descriptorFor)
    {
        Dictionary<int, ClipArchitectureAssignment> assignments = [];
        foreach (ClipSpec clip in spec.Clips)
        {
            IVideoArchitectureModule module = moduleFor(clip);
            VideoArchitectureDescriptor descriptor = descriptorFor(clip);
            ModelProfileId profileId = descriptor.DefaultProfileId;
            Dictionary<int, ResolvedVideoModel> stages = clip.Stages.ToDictionary(
                stage => stage.ClipStageRawIndex,
                stage => new ResolvedVideoModel(
                    stage.Model,
                    descriptor.Id,
                    profileId,
                    descriptor));
            assignments[clip.Id] = new(
                clip.Id,
                module,
                descriptor,
                stages);
        }
        return new(assignments, []);
    }

    private sealed class RecordingWanModule : IVideoArchitectureModule
    {
        internal int CompileCount { get; private set; }

        public VideoArchitectureDescriptor Descriptor =>
            WanArchitectureModule.Instance.Descriptor;

        public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
        {
            resolved = null;
            return false;
        }

        public ArchitectureClipCompilation ValidateAndCompileClip(
            ClipSpec clip,
            IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
            ArchitectureClipCompileContext context)
        {
            CompileCount++;
            throw new InvalidOperationException(
                "A blocked effective request must not reach architecture compilation.");
        }
    }
}
