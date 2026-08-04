using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.MiniMax;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.HostVideo;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class EffectiveVideoRequestTests
{
    [Theory]
    [InlineData(1, 27)]
    [InlineData(4, 29)]
    [InlineData(6, 31)]
    [InlineData(8, 33)]
    public void Temporal_grid_is_applied_only_after_the_selected_model_resolves(
        int frameGrid,
        int expectedFrames)
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model");
        ClipSpec clip = Clip(stage) with { Frames = 27 };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = frameGrid };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        Assert.Equal(27, authored.Clips[0].Frames);
        Assert.Equal(expectedFrames, request.Spec.Clips[0].Frames);
    }

    [Fact]
    public void Full_length_retake_tracks_the_resolved_effective_frame_count()
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model") with
        {
            RetakeWindow = new(2, 25, 0.6),
        };
        ClipSpec clip = Clip(stage) with { Frames = 27 };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        Assert.Equal(25, authored.Clips[0].Stages[0].RetakeWindow.LengthFrames);
        Assert.Equal(31, request.Spec.Clips[0].Stages[0].RetakeWindow.LengthFrames);
    }

    [Fact]
    public void Text_to_video_root_uses_its_model_grid_even_when_control_is_zero()
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model") with
        {
            Control = 0,
        };
        ClipSpec clip = Clip(stage) with { Frames = 27 };
        VideoStagesSpec authored = Spec(clip) with { IsTextToVideo = true };
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        Assert.Equal(33, request.Spec.Clips[0].Frames);
    }

    [Fact]
    public void Passthrough_after_a_init_video_text_to_video_lead_still_replaces_the_text_root()
    {
        StageSpec sourceStage = Stage(0, rawIndex: 0, model: "grid-model") with
        {
            Control = 0,
        };
        StageSpec trailingStage = Stage(0, rawIndex: 0, model: "grid-model") with
        {
            Control = 0,
        };
        ClipSpec source = Clip(sourceStage) with
        {
            Id = 0,
            Frames = 27,
            InitVideo = new("data:video/mp4;base64,AA==", "source.mp4", 0),
        };
        ClipSpec trailing = Clip(trailingStage) with { Id = 1, Frames = 27 };
        VideoStagesSpec authored = Spec(source, trailing) with
        {
            IsTextToVideo = true,
        };
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        Assert.Equal(
            new int?[] { 27, 33 },
            request.Spec.Clips.Select(clip => clip.Frames));
    }

    [Theory]
    [InlineData(true, false, Constants.AudioSourceControlNet)]
    [InlineData(false, true, Constants.AudioSourceNative)]
    public void Runtime_derived_lengths_are_not_projected_onto_a_static_grid(
        bool fromAudio,
        bool fromControl,
        string audioSource)
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model");
        ClipSpec clip = Clip(stage) with
        {
            Frames = 27,
            AudioSource = audioSource,
            ClipLengthFromAudio = fromAudio,
            ClipLengthFromControlNet = fromControl,
        };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        Assert.Equal(27, request.Spec.Clips[0].Frames);
    }

    [Fact]
    public void Audio_length_from_a_non_driving_source_still_projects_onto_the_grid()
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model");
        ClipSpec clip = Clip(stage) with
        {
            Frames = 27,
            AudioSource = Constants.AudioSourceNative,
            ClipLengthFromAudio = true,
        };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        ClipSpec effective = Assert.Single(request.Spec.Clips);
        Assert.Equal(33, effective.Frames);
        Assert.True(effective.ClipLengthFromAudio);
    }

    [Fact]
    public void Disallowed_audio_source_still_projects_onto_the_MiniMax_grid()
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "minimax-model");
        ClipSpec clip = Clip(stage) with
        {
            Frames = 27,
            AudioSource = "future-audio-source",
            ClipLengthFromAudio = true,
        };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            MiniMaxArchitectureModule.Instance.Descriptor;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => MiniMaxArchitectureModule.Instance, _ => descriptor));

        Assert.Equal(39, Assert.Single(request.Spec.Clips).Frames);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Stale_runtime_derived_lengths_still_project_onto_the_resolved_grid(
        bool fromAudio,
        bool fromControl)
    {
        ClipSpec clip = Clip(Stage(0, rawIndex: 0)) with
        {
            Frames = 27,
            ClipLengthFromAudio = fromAudio,
            ClipLengthFromControlNet = fromControl,
        };
        VideoStagesSpec authored = Spec(clip);

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            ResolveWan(authored));

        ClipSpec effective = Assert.Single(request.Spec.Clips);
        Assert.Equal(4, WanArchitectureModule.Instance.Descriptor.FrameGrid);
        Assert.Equal(29, effective.Frames);
        Assert.Equal(fromAudio, effective.ClipLengthFromAudio);
        Assert.Equal(fromControl, effective.ClipLengthFromControlNet);
    }

    [Fact]
    public void InitVideo_passthrough_stages_do_not_change_the_source_frame_count()
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model") with
        {
            Control = 0,
        };
        ClipSpec clip = Clip(stage) with
        {
            Frames = 27,
            InitVideo = new("data:video/mp4;base64,AA==", "source.mp4", 0),
        };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        Assert.Equal(27, request.Spec.Clips[0].Frames);
    }

    [Fact]
    public void Unsupported_retake_remains_authored_without_activating_the_temporal_grid()
    {
        StageSpec stage = Stage(0, rawIndex: 0) with
        {
            Control = 0,
            RetakeWindow = new(0, 28, 0.6),
        };
        ClipSpec clip = Clip(stage) with
        {
            Frames = 28,
            InitVideo = new("data:video/mp4;base64,AA==", "source.mp4", 0),
        };
        VideoStagesSpec authored = Spec(clip);
        ArchitecturePlanningResult architectures = ResolveWan(authored);

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            architectures);

        Assert.Equal(28, request.Spec.Clips[0].Frames);
        Assert.Equal(stage.RetakeWindow, request.Spec.Clips[0].Stages[0].RetakeWindow);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            authored,
            RootEnvironment.FromSpec(authored),
            architectures);
        Assert.True(plan.Clips[0].Stages[0].IsPassthrough);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "effective-request.unsupported-retake-ignored");
    }

    [Fact]
    public void Admission_blocked_clips_do_not_apply_the_temporal_grid()
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model");
        ClipSpec clip = Clip(stage) with { Frames = 27 };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;
        ArchitecturePlanningResult resolved =
            Resolve(authored, _ => module, _ => descriptor);
        ArchitecturePlanningResult blocked = resolved with
        {
            Diagnostics =
            [
                new(
                    PlanDiagnosticSeverity.Error,
                    "architecture-authored-stage-model-unresolved",
                    "Skipped suffix model is unavailable.",
                    clip.Id,
                    RawStageIndex: 1),
            ],
        };

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(authored, blocked);

        Assert.Equal(27, request.Spec.Clips[0].Frames);
    }

    [Fact]
    public void Unrepresentable_aligned_frame_count_warns_and_keeps_authored_frames()
    {
        StageSpec stage = Stage(0, rawIndex: 0, model: "grid-model");
        ClipSpec clip = Clip(stage) with { Frames = int.MaxValue };
        VideoStagesSpec authored = Spec(clip);
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor with { FrameGrid = 8 };
        IVideoArchitectureModule module = Ltx2ArchitectureModule.Instance;

        EffectiveVideoRequest request = EffectiveVideoRequestProjector.Project(
            authored,
            Resolve(authored, _ => module, _ => descriptor));

        Assert.Equal(int.MaxValue, request.Spec.Clips[0].Frames);
        Assert.Contains(
            request.Diagnostics,
            diagnostic => diagnostic.Code
                    == "effective-request.temporal-frame-count-overflow"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Post_resolution_preserves_authored_values_and_warns_for_stale_hints()
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
            AuthoredArchitectureHint = "stale-architecture",
            AuthoredModelProfileHint = "stale-profile",
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
        Assert.Equal("stale-architecture", clip.AuthoredArchitectureHint);
        Assert.Equal("stale-profile", clip.AuthoredModelProfileHint);
        Assert.Single(clip.IcLoras);
        Assert.Equal(2, clip.Stages[1].Upscale);
        Assert.Equal([0.8], clip.Stages[1].IcLoraStrengths);

        Assert.Equal("stale-architecture", effective.AuthoredArchitectureHint);
        Assert.Equal("stale-profile", effective.AuthoredModelProfileHint);
        Assert.Equal(
            ["old-first-profile", "old-second-profile"],
            effective.AuthoredStages.Select(stage => stage.ModelProfileId));
        Assert.Single(effective.IcLoras);
        Assert.Equal([0.8], effective.Stages[1].IcLoraStrengths);
        Assert.Equal(2, effective.Stages[1].Upscale);
        Assert.Equal(
            "latentmodel-detail.safetensors",
            effective.Stages[1].UpscaleMethod);
        Assert.Equal(
            4,
            request.Diagnostics.Count(diagnostic =>
                diagnostic.Code.Contains("stale", StringComparison.Ordinal)));
        Assert.All(
            request.Diagnostics,
            diagnostic => Assert.Equal(
                PlanDiagnosticSeverity.Warning,
                diagnostic.Severity));
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
            diagnostic => diagnostic.Code == "effective-request.unsupported-ic-lora-ignored");
        StockHostVideoStagePayload payload = plan.Clips[0].Stages[1]
            .RequireStockHostVideoPayload(WanArchitectureModule.ArchitectureId, "Wan");
        Assert.Equal(StageUpscaleMode.Model, payload.Core.Upscale.Mode);
        Assert.Equal(2, payload.Core.Upscale.Factor);
    }

    [Fact]
    public void Capability_pass_warns_for_stage_only_reference_payload_without_erasing_it()
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
            AuthoredArchitectureHint =
                HostVideoArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileHint =
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

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            authored,
            RootEnvironment.FromSpec(authored),
            architectures);

        Assert.Equal([0.7], authored.Clips[0].Stages[0].ImageRefStrengths);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                    == "effective-request.unsupported-frame-references-ignored"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Host_capability_warnings_do_not_erase_authored_values()
    {
        StageSpec first = Stage(
            0,
            rawIndex: 0,
            upscale: 2,
            upscaleMethod: "latentmodel-detail.safetensors",
            model: "host-model") with
        {
            IcLoraStrengths = [0.8],
            ImageRefStrengths = [0.7],
            Loras = [new("stage-lora.safetensors", 0.5)],
            RetakeWindow = new(0, 8, 0.6),
        };
        StageSpec second = Stage(
            1,
            rawIndex: 1,
            model: "host-model");
        ClipSpec clip = Clip(first, second) with
        {
            AuthoredArchitectureHint =
                HostVideoArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileHint =
                HostVideoArchitectureModule.ProfileId.Value,
            AuthoredStages =
            [
                new(0, first.Model, HostVideoArchitectureModule.ProfileId.Value, false),
                new(1, second.Model, HostVideoArchitectureModule.ProfileId.Value, false),
            ],
            AudioSource = Constants.AudioSourceUpload,
            SaveAudioTrack = true,
            ClipLengthFromAudio = true,
            ClipLengthFromControlNet = true,
            ReuseAudio = true,
            UploadedAudio = new("data:audio/wav;base64,QUJD", "audio.wav"),
            ImageRefs =
            [
                new(
                    Constants.IcLoraSourceUpload,
                    1,
                    false,
                    "reference.png",
                    "data:image/png;base64,QUJD"),
            ],
            PromptWindows = [new("later", 1, 1)],
            ReferenceFraming = ReferenceFramingMode.Fit,
            InitVideo = new(
                "data:video/mp4;base64,QUJD",
                "source.mp4",
                0),
            Loras = [new("clip-lora.safetensors", 0.5)],
            IcLoras =
            [
                new(
                    "host-ic.safetensors",
                    Constants.IcLoraSourceUpload,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null),
            ],
        };
        VideoStagesSpec authored = Spec(clip);
        ArchitecturePlanningResult architectures = Resolve(
            authored,
            _ => HostVideoArchitectureModule.Instance,
            _ => HostVideoArchitectureModule.Instance.Descriptor);

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(authored, architectures);
        ClipSpec effective = Assert.Single(request.Spec.Clips);
        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                effective,
                HostVideoArchitectureModule.Instance.Descriptor,
                ArchitectureEntryMode.InitVideo);

        Assert.Equal(
            ArchitectureFeature.None,
            HostVideoArchitectureModule.Instance.Descriptor.Features);
        Assert.Equal(clip.ImageRefs, effective.ImageRefs);
        Assert.Equal(first.ImageRefStrengths, effective.Stages[0].ImageRefStrengths);
        Assert.Equal(ReferenceFramingMode.Fit, effective.ReferenceFraming);
        Assert.Equal(first.RetakeWindow, effective.Stages[0].RetakeWindow);
        Assert.Equal(clip.PromptWindows, effective.PromptWindows);
        Assert.Equal(Constants.AudioSourceUpload, effective.AudioSource);
        Assert.True(effective.SaveAudioTrack);
        Assert.True(effective.ClipLengthFromAudio);
        Assert.True(effective.ClipLengthFromControlNet);
        Assert.True(effective.ReuseAudio);
        Assert.Equal(clip.UploadedAudio, effective.UploadedAudio);
        Assert.Equal(clip.IcLoras, effective.IcLoras);
        Assert.Equal(first.IcLoraStrengths, effective.Stages[0].IcLoraStrengths);
        Assert.Equal(2, effective.Stages[0].Upscale);
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code
                    == "effective-request.unsupported-ic-lora-ignored"
                && diagnostic.Message.Contains(
                    "Host Video does not support",
                    StringComparison.Ordinal));
        Assert.Equal(2, effective.Stages.Count);
        Assert.Equal(clip.InitVideo, effective.InitVideo);
        Assert.Equal(clip.Loras, effective.Loras);
        Assert.Equal(first.Loras, effective.Stages[0].Loras);
    }

    [Fact]
    public void Wan_absent_capabilities_derive_ignore_dispositions_without_frame_references()
    {
        Assert.Equal(
            ArchitectureFeature.FrameReferences,
            WanArchitectureModule.Instance.Descriptor.Features);
    }

    [Fact]
    public void Unsupported_non_cut_boundary_is_normalized_only_in_the_boundary_plan()
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
        Assert.Equal(Constants.BoundaryOutCrossfade, effectiveLeft.BoundaryOut);
        Assert.Equal(16, effectiveLeft.BoundaryOutOverlap);
        Assert.True(effectiveLeft.BoundaryOutCarryAudio);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            authored,
            RootEnvironment.FromSpec(authored),
            architectures);
        BoundaryPlan boundary = Assert.Single(plan.Boundaries);
        Assert.Equal(BoundaryJoinType.Cut, boundary.Effective);
        Assert.Equal(
            BoundaryFallbackReason.ArchitectureRuleUnsupported,
            boundary.Fallback);
        Assert.False(boundary.CarryAudio);
        // The cross-architecture branch is the sole reporter: no generic
        // "boundary-architectureruleunsupported" follow-up, and nothing from the projector.
        PlanDiagnostic reported = Assert.Single(plan.Diagnostics);
        Assert.Equal("boundary-cross-architecture-non-cut", reported.Code);
        Assert.Equal(PlanDiagnosticSeverity.Warning, reported.Severity);
        Assert.Empty(request.Diagnostics);
    }

    [Fact]
    public void Request_global_video_swap_emits_one_common_warning()
    {
        StageSpec hostStage = Stage(0, rawIndex: 0, model: "host-model");
        ClipSpec host = Clip(hostStage) with
        {
            AuthoredArchitectureHint =
                HostVideoArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileHint =
                HostVideoArchitectureModule.ProfileId.Value,
            AuthoredStages =
            [
                new(
                    0,
                    hostStage.Model,
                    HostVideoArchitectureModule.ProfileId.Value,
                    false),
            ],
        };
        ClipSpec wan = Clip(Stage(0, rawIndex: 0)) with { Id = 1 };
        VideoStagesSpec authored = Spec(host, wan) with
        {
            LegacyVideoSwap = new("legacy-swap-model"),
        };
        ArchitecturePlanningResult architectures = Resolve(
            authored,
            clip => clip.Id == 0
                ? HostVideoArchitectureModule.Instance
                : WanArchitectureModule.Instance,
            clip => clip.Id == 0
                ? HostVideoArchitectureModule.Instance.Descriptor
                : WanArchitectureModule.Instance.Descriptor);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            authored,
            RootEnvironment.FromSpec(authored),
            architectures);

        PlanDiagnostic warning = Assert.Single(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "effective-request.video-swap-ignored");
        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void Unknown_Wan_upscale_warns_without_sanitizing_authored_data()
    {
        StageSpec first = Stage(0, rawIndex: 0);
        StageSpec unknown = Stage(
            1,
            rawIndex: 1,
            upscale: 2,
            upscaleMethod: "future-upscale");
        VideoStagesSpec spec = Spec(Clip(first, unknown));
        ArchitecturePlanningResult architectures = ResolveWan(spec);

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(spec, architectures);

        Assert.Equal(2, request.Spec.Clips[0].Stages[1].Upscale);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architectures);
        PlanDiagnostic warning = Assert.Single(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "effective-request.unknown-upscale");
        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.NotNull(plan.Clips[0].ArchitecturePayload);
    }

    [Fact]
    public void Prefix_only_Wan_upscale_warns_and_compiles()
    {
        StageSpec first = Stage(0, rawIndex: 0);
        StageSpec malformed = Stage(
            1,
            rawIndex: 1,
            upscale: 2,
            upscaleMethod: "pixel-   ");
        VideoStagesSpec spec = Spec(Clip(first, malformed));
        ArchitecturePlanningResult architectures = ResolveWan(spec);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architectures);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "effective-request.unknown-upscale"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Baseline_control_strength_does_not_warn_without_IcLora_configuration()
    {
        StageSpec stage = Stage(
            0,
            rawIndex: 0,
            model: "host-model") with
        {
            ControlNetStrength = Constants.DefaultStageControlNetStrength,
        };
        VideoStagesSpec spec = Spec(Clip(stage));
        ArchitecturePlanningResult architectures = Resolve(
            spec,
            _ => HostVideoArchitectureModule.Instance,
            _ => HostVideoArchitectureModule.Instance.Descriptor);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architectures);

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-ic-lora-ignored");
        Assert.Equal(
            Constants.DefaultStageControlNetStrength,
            spec.Clips[0].Stages[0].ControlNetStrength);
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
        clip["architectureHint"] = "old-wan-cache";
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
        Assert.Contains(warnings, warning => warning.Contains("IC-LoRA data"));
        ClipSpec authored = Assert.Single(generator.GetVideoStagesSpec().Clips);
        Assert.Equal("old-wan-cache", authored.AuthoredArchitectureHint);
        Assert.Equal("old-wan-profile", authored.AuthoredModelProfileHint);
        Assert.Single(authored.IcLoras);
        Assert.Equal(2, authored.Stages[1].Upscale);
        Assert.Equal([0.8], authored.Stages[1].IcLoraStrengths);
        Assert.Equal(
            StageUpscaleMode.Latent,
            plan.Clips[0].Stages[1].Core.Upscale.Mode);
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
            AuthoredArchitectureHint = WanArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileHint = WanArchitectureModule.ImageToVideoProfileId.Value,
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
            AuthoredArchitectureHint = Ltx2ArchitectureModule.ArchitectureId.Value,
            AuthoredModelProfileHint = "ltx-2.3",
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
            ModelProfileId profileId = descriptor.Id == Ltx2ArchitectureModule.ArchitectureId
                ? Ltx2ArchitectureModule.ProfileId
                : descriptor.Id == WanArchitectureModule.ArchitectureId
                    ? WanArchitectureModule.ImageToVideoProfileId
                    : HostVideoArchitectureModule.ProfileId;
            Dictionary<int, ResolvedVideoModel> stages = clip.Stages.ToDictionary(
                stage => stage.ClipStageRawIndex,
                stage => TestResolvedVideoModel.Create(
                    stage.Model,
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

}
