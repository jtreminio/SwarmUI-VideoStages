using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ArchitectureFoundationTests
{
    [Fact]
    public void Identity_types_are_normalized_and_distinct()
    {
        Assert.Equal(new ArchitectureId("ltx2"), new ArchitectureId(" LTX2 "));
        Assert.Equal("ltx-2.3", new ModelProfileId(" LTX-2.3 ").Value);
        Assert.NotEqual(new ArchitectureId("ltx2"), new ArchitectureId("fake"));
    }

    [Fact]
    public void Resolver_uses_authored_stage_zero_and_validates_skipped_stages()
    {
        FakeRegistry registry = new();
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: false),
                new(1, "fake-model", "fake-profile", Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(clip), registry);

        Assert.Equal(new ArchitectureId("ltx2"), result.Clips[0].Architecture.Id);
        PlanDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "architecture-mixed-authored-stage-clip");
        Assert.Contains("(skipped)", diagnostic.Message);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Effective_request_ignores_a_stale_persisted_clip_identity_hint()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureHint = "fake",
            AuthoredStages = [new(0, "ltx-model", "ltx-profile", Skipped: false)],
        };

        VideoStagesSpec spec = Spec(clip);
        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry());
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            result);

        Assert.DoesNotContain(
            result.Diagnostics,
            item => item.Code == "architecture-authored-identity-mismatch");
        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == "effective-request.stale-architecture-hint"
                && item.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Resolver_rejects_a_model_forbidden_to_the_request_session()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredStages = [new(0, "ltx-model", "ltx-profile", Skipped: false)],
        };

        ArchitecturePlanningResult result = ArchitecturePlanResolver.Resolve(
            Spec(clip),
            new FakeRegistry(),
            RestrictedSession("ltx-model"));

        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "architecture-stage0-model-unresolved"
                && item.Message.Contains("'ltx-model'"));
        Assert.False(result.Clips.ContainsKey(clip.Id));
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Registry_authorization_is_skipped_only_without_a_request_user()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        IVideoArchitectureRegistry production = VideoArchitectureRegistry.Production;
        IVideoArchitectureRegistry restricted =
            production.ForSession(RestrictedSession(models.VideoModel.Name));

        // A missing request user means there is no authorization context to apply.
        Assert.Same(production, production.ForSession(null));
        Assert.Same(production, production.ForSession(new Session()));
        Assert.Contains(
            production.ResolvedModels,
            resolved => resolved.ModelName == models.VideoModel.Name);
        Assert.DoesNotContain(
            restricted.ResolvedModels,
            resolved => resolved.ModelName == models.VideoModel.Name);
    }

    [Fact]
    public void Resolver_refuses_the_extensionless_spelling_of_a_forbidden_model()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        // Core resolves both spellings; the blacklist only matches the string it is given.
        string canonicalName = models.VideoModel.Name;
        string authoredName = Path.GetFileNameWithoutExtension(canonicalName);
        ClipSpec clip = GeneratedClip(0, Stage(10, authoredName)) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredStages = [new(0, authoredName, "ltx-2.3", Skipped: false)],
        };

        Assert.True(ArchitecturePlanResolver.Resolve(
            Spec(clip),
            VideoArchitectureRegistry.Production,
            RestrictedSession("unrelated-model")).Clips.ContainsKey(clip.Id));

        ArchitecturePlanningResult restricted = ArchitecturePlanResolver.Resolve(
            Spec(clip),
            VideoArchitectureRegistry.Production,
            RestrictedSession(canonicalName));

        Assert.Contains(
            restricted.Diagnostics,
            item => item.Code == "architecture-stage0-model-unresolved");
        Assert.False(restricted.Clips.ContainsKey(clip.Id));
    }

    [Fact]
    public void Effective_request_warns_without_rewriting_skipped_stage_profile_hints()
    {
        ClipSpec inactive = GeneratedClip(0) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredModelProfileHint = "ltx-profile",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
                new(3, "ltx-model", "wrong-profile", Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(inactive), new FakeRegistry());

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(Spec(inactive), result);
        PlanDiagnostic diagnostic = Assert.Single(
            request.Diagnostics,
            item => item.Code == "effective-request.stale-stage-profile-hint");
        Assert.Contains("stage 3", diagnostic.Message);
        Assert.Equal(3, diagnostic.RawStageIndex);
        Assert.Equal(
            "wrong-profile",
            request.Spec.Clips[0].AuthoredStages[1].ModelProfileId);
        Assert.Equal(
            "wrong-profile",
            inactive.AuthoredStages[1].ModelProfileId);
        Assert.Equal(new ArchitectureId("ltx2"), result.Clips[0].Architecture.Id);
    }

    [Fact]
    public void Resolver_validates_all_skipped_init_video_chain_before_executing_as_none()
    {
        ClipSpec initVideoClip = InitVideoClip(0) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredModelProfileHint = "ltx-profile",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
                new(4, "fake-model", "fake-profile", Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(initVideoClip), new FakeRegistry());

        Assert.Equal(NoneArchitecture.Id, result.Clips[0].Architecture.Id);
        PlanDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "architecture-mixed-authored-stage-clip");
        Assert.Equal(4, diagnostic.RawStageIndex);
        Assert.Contains("stage 4 (skipped)", diagnostic.Message);
    }

    [Fact]
    public void Source_only_clip_warns_about_stale_generation_identity_hints()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredModelProfileHint = "ltx-profile",
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(clip), new FakeRegistry());

        Assert.Equal(NoneArchitecture.Id, result.Clips[0].Architecture.Id);
        Assert.All(
            result.Diagnostics,
            item => Assert.Equal(PlanDiagnosticSeverity.Warning, item.Severity));
        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "architecture-source-only-identity-mismatch");
        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "architecture-source-only-profile-mismatch");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Resolver_warns_for_an_unresolved_skipped_stage()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: false),
                new(1, "missing-model", null, Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(clip), new FakeRegistry());

        PlanDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "architecture-authored-stage-model-unresolved");
        Assert.Equal(PlanDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.False(result.HasErrors);
        Assert.True(result.Clips.ContainsKey(clip.Id));
    }

    [Fact]
    public void Resolver_treats_an_undeclared_model_profile_as_a_migration_alias()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            AuthoredStages = [new(0, "fake-model", "ghost-profile", false)],
        };

        ArchitecturePlanningResult result = ArchitecturePlanResolver.Resolve(
            Spec(clip),
            new FakeRegistry(undeclaredFakeProfile: true));

        Assert.DoesNotContain(
            result.Diagnostics,
            item => item.Code == "architecture-resolved-profile-not-declared");
        Assert.Equal(
            "ghost-profile",
            result.Clips[clip.Id].StageModels[0].ModelProfileId.Value);
    }

    [Fact]
    public void InitVideo_only_dormant_mixed_stages_report_only_the_dormant_architecture_error()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            AuthoredArchitectureHint = "none",
            AuthoredModelProfileHint = "none",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
                new(3, "fake-model", "fake-profile", Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(clip), new FakeRegistry());

        Assert.Equal(NoneArchitecture.Id, result.Clips[0].Architecture.Id);
        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "architecture-mixed-authored-stage-clip");
        Assert.DoesNotContain(
            result.Diagnostics,
            item => item.Code is "architecture-authored-identity-mismatch"
                or "architecture-authored-profile-mismatch"
                or "architecture-source-only-identity-mismatch"
                or "architecture-source-only-profile-mismatch");
    }

    [Fact]
    public void InitVideo_only_same_architecture_dormant_stages_plan_as_none_without_identity_error()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            AuthoredArchitectureHint = "none",
            AuthoredModelProfileHint = "none",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
                new(2, "ltx-model", "ltx-profile", Skipped: true),
            ],
        };
        VideoStagesSpec spec = Spec(clip);
        ArchitecturePlanningResult architecture =
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry());
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architecture);

        Assert.Equal(NoneArchitecture.Id, Assert.Single(plan.Clips).Architecture.Id);
        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Code.Contains("identity-mismatch", StringComparison.Ordinal)
                || item.Code.Contains("profile-mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void InitVideo_only_same_architecture_dormant_stages_may_use_different_profiles()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            AuthoredArchitectureHint = "none",
            AuthoredModelProfileHint = "none",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
                new(2, "ltx23-model", "ltx-2.3-profile", Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(clip), new FakeRegistry());

        Assert.Equal(NoneArchitecture.Id, result.Clips[0].Architecture.Id);
        Assert.DoesNotContain(
            result.Diagnostics,
            item => item.Code == "architecture-mixed-authored-stage-profile-clip");
        Assert.DoesNotContain(
            result.Diagnostics,
            item => item.Code == "architecture-mixed-authored-stage-clip");
    }

    [Fact]
    public void Cross_architecture_non_cut_boundary_warns_and_uses_an_effective_cut()
    {
        ClipSpec left = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredStages = [new(0, "ltx-model", "ltx-profile", false)],
        };
        left = left with { BoundaryOut = Constants.BoundaryOutCrossfade };
        ClipSpec right = GeneratedClip(1, Stage(11, "fake-model")) with
        {
            AuthoredArchitectureHint = "fake",
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };
        VideoStagesSpec spec = Spec(left, right);
        ArchitecturePlanningResult architecture =
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry());

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architecture);

        BoundaryPlan boundary = Assert.Single(plan.Boundaries);
        Assert.Equal(BoundaryJoinType.Crossfade, boundary.Requested);
        Assert.Equal(BoundaryJoinType.Cut, boundary.Effective);
        Assert.Equal(
            BoundaryFallbackReason.ArchitectureRuleUnsupported,
            boundary.Fallback);
        Assert.Single(
            plan.Diagnostics,
            item => item.Code == "boundary-cross-architecture-non-cut"
                && item.Severity == PlanDiagnosticSeverity.Warning);
        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(Constants.BoundaryOutCrossfade, left.BoundaryOut);
    }

    [Fact]
    public void Plan_carries_clip_architecture_and_stage_profile_identities()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredStages = [new(0, "ltx-model", "ltx-profile", false)],
        };
        VideoStagesSpec spec = Spec(clip);
        ArchitecturePlanningResult architecture =
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry());

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architecture);

        ClipPlan plannedClip = Assert.Single(plan.Clips);
        Assert.Equal(new ArchitectureId("ltx2"), plannedClip.Architecture.Id);
        Assert.Equal(
            new ModelProfileId("ltx-profile"),
            Assert.Single(plannedClip.Stages).ResolvedModel.ModelProfileId);
    }

    [Fact]
    public void Generated_multistage_clip_allows_different_profiles_within_one_architecture()
    {
        StageSpec first = Stage(10, "ltx-model") with
        {
            ClipStageIndex = 0,
            ClipStageRawIndex = 0,
        };
        StageSpec second = Stage(11, "ltx23-model") with
        {
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };
        ClipSpec clip = GeneratedClip(0, first, second) with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredModelProfileHint = "ltx-profile",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: false),
                new(1, "ltx23-model", "ltx-2.3-profile", Skipped: false),
            ],
        };
        VideoStagesSpec spec = Spec(clip);
        ArchitecturePlanningResult architecture =
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry());

        Assert.Equal(new ArchitectureId("ltx2"), architecture.Clips[0].Architecture.Id);
        Assert.DoesNotContain(
            architecture.Diagnostics,
            diagnostic => diagnostic.Code.Contains("architecture", StringComparison.Ordinal));

        ClipPlan plannedClip = Assert.Single(VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            architecture).Clips);
        Assert.Equal(
            ["ltx-profile", "ltx-2.3-profile"],
            plannedClip.Stages
                .Select(stage => stage.ResolvedModel.ModelProfileId.Value)
                .ToArray());
        Assert.Equal(
            "ltx-profile",
            architecture.Clips[0].StageModels[0].ModelProfileId.Value);
    }

    [Fact]
    public void Common_projection_keeps_stage_loras_for_every_architecture()
    {
        FakeRegistry registry = new();
        StageSpec stage = Stage(10, "fake-model") with
        {
            Loras = [new LoraRef("ltx-owned-option.safetensors")],
        };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };
        VideoStagesSpec spec = Spec(clip);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ArchitecturePlanResolver.Resolve(spec, registry));

        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Message.Contains("LoRA"));
        Assert.Equal(1, registry.CompileCounts[new ArchitectureId("fake")]);
        Assert.NotNull(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Fact]
    public void Common_projection_ignores_nondefault_reference_framing()
    {
        FakeRegistry registry = new();
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            ReferenceFraming = ReferenceFramingMode.FitGreen,
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };

        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            item => item.Code
                    == "effective-request.unsupported-reference-framing-ignored"
                && item.Message.Contains("reference framing"));
        Assert.Equal(1, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_does_not_count_skipped_dormant_stage_as_active()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor();
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            AuthoredStages =
            [
                new(0, "fake-model", "fake-profile", false),
                new(2, "fake-model", "fake-profile", true),
            ],
        };
        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Code == "architecture-capability-unsupported");
        Assert.Equal(1, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Source_only_none_ignores_dormant_generation_authoring_options()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            PromptWindows = [new("dormant relay", 0, 1)],
            ImageRefs = [new("Upload", 1, false, "ref.png")],
            Loras = [new("clip-lora.safetensors")],
            IcLoras =
            [
                new(
                    "ic-lora.safetensors",
                    Constants.IcLoraSourceUpload,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null),
            ],
            AuthoredArchitectureHint = "none",
            AuthoredModelProfileHint = "none",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
            ],
        };

        VideoExecutionPlan plan = Compile(clip, new FakeRegistry());

        Assert.Equal(NoneArchitecture.Id, Assert.Single(plan.Clips).Architecture.Id);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported");
    }

    [Fact]
    public void Source_only_none_ignores_captured_stage_audio_reuse()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            ReuseAudio = true,
            AuthoredArchitectureHint = "none",
            AuthoredModelProfileHint = "none",
        };

        VideoExecutionPlan plan = Compile(clip, new FakeRegistry());

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                    == "effective-request.unsupported-audio-reuse-ignored"
                && diagnostic.Message.Contains("captured stage audio reuse"));
        Assert.NotNull(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Fact]
    public void Source_only_none_ignores_audio_derived_duration_but_accepts_uploaded_audio()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            AudioSource = Constants.AudioSourceUpload,
            UploadedAudio = new("data:audio/wav;base64,AA==", "voice.wav"),
            SaveAudioTrack = true,
            ClipLengthFromAudio = true,
            AuthoredArchitectureHint = "none",
            AuthoredModelProfileHint = "none",
        };

        VideoExecutionPlan plan = Compile(clip, new FakeRegistry());

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                    == "effective-request.unsupported-audio-derived-duration-ignored"
                && diagnostic.Message.Contains("audio-derived clip duration"));
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Message.Contains("clip audio source"));
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-audio-output-ignored");
        Assert.NotNull(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Fact]
    public void Source_only_none_ignores_control_signal_derived_duration()
    {
        ClipSpec clip = InitVideoClip(0) with
        {
            ClipLengthFromControlNet = true,
            AuthoredArchitectureHint = "none",
            AuthoredModelProfileHint = "none",
        };

        VideoExecutionPlan plan = Compile(clip, new FakeRegistry());

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "effective-request.unsupported-ic-lora-ignored"
                && diagnostic.Message.Contains("control-signal-derived clip duration"));
        Assert.NotNull(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Theory]
    [InlineData(Constants.AudioSourceUpload)]
    [InlineData(Constants.AudioSourceControlNet)]
    [InlineData("audio3")]
    public void Ltx_accepts_audio_derived_duration_from_external_source_kinds(
        string audioSource)
    {
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor;
        StageSpec stage = Stage(10, "ltx-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            AudioSource = audioSource,
            UploadedAudio = audioSource == Constants.AudioSourceUpload
                ? new("data:audio/wav;base64,AA==", "voice.wav")
                : null,
            ClipLengthFromAudio = true,
        };
        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                clip,
                descriptor,
                ArchitectureEntryMode.ImageToVideo);

        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Ltx_rejects_native_audio_as_audio_derived_duration_source()
    {
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor;
        StageSpec stage = Stage(10, "ltx-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ClipLengthFromAudio = true,
        };
        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                clip,
                descriptor,
                ArchitectureEntryMode.ImageToVideo);

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code
                    == "audio.length.source_cannot_drive_duration"
                && diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Message.Contains("audio-derived clip duration"));
    }

    [Fact]
    public void ControlNet_length_precedence_suppresses_audio_duration_source_validation()
    {
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor;
        StageSpec stage = Stage(10, "ltx-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ClipLengthFromAudio = true,
            ClipLengthFromControlNet = true,
        };

        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                clip,
                descriptor,
                ArchitectureEntryMode.ImageToVideo);

        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Code == "audio.length.source_cannot_drive_duration");
    }

    [Fact]
    public void Audio_derived_duration_leaves_unknown_source_diagnostics_to_audio_planning()
    {
        VideoArchitectureDescriptor descriptor =
            Ltx2ArchitectureModule.Instance.Descriptor;
        StageSpec stage = Stage(10, "ltx-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            AudioSource = "future-audio-source",
            ClipLengthFromAudio = true,
        };
        Dictionary<int, ResolvedVideoModel> stageModels = new()
        {
            [stage.ClipStageRawIndex] = TestResolvedVideoModel.Create(
                stage.Model,
                Ltx2ArchitectureModule.ProfileId,
                descriptor),
        };

        VideoStagesSpec spec = Spec(clip);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            new(
                new Dictionary<int, ClipArchitectureAssignment>
                {
                    [clip.Id] = new(
                        clip.Id,
                        Ltx2ArchitectureModule.Instance,
                        descriptor,
                        stageModels),
                },
                []));

        Assert.Single(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == AudioBaseSourcePlanCompiler.UnknownSourceCode);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith(
                "audio.length.",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Common_projection_ignores_unsupported_frame_references()
    {
        FakeRegistry registry = new(fakeDescriptor: FakeCapabilityDescriptor());
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            ImageRefs = [new("Upload", 1, false, "ref.png")],
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };

        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                    == "effective-request.unsupported-frame-references-ignored"
                && diagnostic.Message.Contains("frame references"));
        Assert.Equal(1, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Common_projection_keeps_every_known_upscale_mode()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor();
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(
            0,
            Stage(10, "fake-model") with
            {
                Upscale = 2,
                UpscaleMethod = "model-fake",
            }) with
        {
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };
        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Message.Contains("upscale"));
        Assert.Equal(1, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_rejects_unsupported_entry_mode()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor(
            entryModes: [ArchitectureEntryMode.TextToVideo]);
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };
        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == "architecture-capability-unsupported"
                && item.Message.Contains("'image-to-video entry'"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Theory]
    [InlineData("ltx2,fake")]
    [InlineData("fake,ltx2")]
    [InlineData("ltx2,fake,ltx2,fake")]
    public void Runtime_dispatches_each_clip_session_across_alternating_cut_boundaries(
        string architectureOrder)
    {
        string[] ids = architectureOrder.Split(',');
        ClipSpec[] clips = [
            .. ids.Select((id, index) => GeneratedClip(
                index,
                Stage(index, id == "ltx2" ? "ltx-model" : "fake-model")) with
            {
                AuthoredArchitectureHint = id,
                AuthoredModelProfileHint = $"{(id == "ltx2" ? "ltx" : "fake")}-profile",
                AuthoredStages =
                [
                    new(
                        index * 2,
                        id == "ltx2" ? "ltx-model" : "fake-model",
                        $"{(id == "ltx2" ? "ltx" : "fake")}-profile",
                        false),
                ],
                BoundaryOut = Constants.BoundaryOutCut,
            })
        ];
        VideoStagesSpec spec = Spec(clips);
        FakeRegistry registry = new();
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ArchitecturePlanResolver.Resolve(spec, registry));
        List<string> calls = [];
        using ArchitectureRuntimeDispatcher dispatcher = new(
        [
            new RecordingSession(new("ltx2"), calls),
            new RecordingSession(new("fake"), calls),
        ]);

        DecodedClipArtifact[] outputs = [
            .. plan.Clips.Select((clip, index) => dispatcher.Execute(new ArchitectureClipRuntimeContext(
                clip,
                index,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)))
        ];

        Assert.All(plan.Boundaries, boundary =>
        {
            Assert.Equal(BoundaryJoinType.Cut, boundary.Requested);
            Assert.Equal(BoundaryJoinType.Cut, boundary.Effective);
        });
        Assert.Equal(ids, calls);
        Assert.Equal(
            ids,
            outputs.Select(output => output.ArchitectureId.Value).ToArray());
    }

    [Fact]
    public void Runtime_dispatcher_rejects_mismatched_session_result_clip()
    {
        VideoExecutionPlan plan = Plan(GeneratedClip(7, Stage(10, "ltx-model")));
        ClipPlan clip = Assert.Single(plan.Clips);
        using ArchitectureRuntimeDispatcher dispatcher = new(
        [
            new ProjectingSession(
                new("ltx2"),
                context => ValidArtifact(context.Clip) with { ClipId = 88 }),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Execute(new ArchitectureClipRuntimeContext(
                clip,
                0,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)));

        Assert.Contains("artifact for clip '88'", error.Message);
        Assert.Contains("planned clip '7'", error.Message);
    }

    [Fact]
    public void Runtime_dispatcher_rejects_mismatched_session_result_architecture()
    {
        VideoExecutionPlan plan = Plan(GeneratedClip(7, Stage(10, "ltx-model")));
        ClipPlan clip = Assert.Single(plan.Clips);
        using ArchitectureRuntimeDispatcher dispatcher = new(
        [
            new ProjectingSession(
                new("ltx2"),
                context => ValidArtifact(context.Clip) with
                {
                    ArchitectureId = new("fake"),
                }),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Execute(new ArchitectureClipRuntimeContext(
                clip,
                0,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)));

        Assert.Contains("Architecture 'ltx2'", error.Message);
        Assert.Contains("artifact for architecture 'fake'", error.Message);
        Assert.Contains("planned architecture 'ltx2'", error.Message);
        Assert.Contains("clip '7'", error.Message);
    }

    [Theory]
    [InlineData((int)DecodedMediaKind.Audio, 512, 512, 24, 25)]
    [InlineData((int)DecodedMediaKind.Video, 0, 512, 24, 25)]
    [InlineData((int)DecodedMediaKind.Video, 512, 0, 24, 25)]
    [InlineData((int)DecodedMediaKind.Video, 512, 512, 0, 25)]
    [InlineData((int)DecodedMediaKind.Video, 512, 512, 24, 0)]
    public void Runtime_dispatcher_rejects_mistyped_or_non_positive_video_artifact(
        int rawVideoKind,
        int width,
        int height,
        int framesPerSecond,
        int frames)
    {
        VideoExecutionPlan plan = Plan(GeneratedClip(7, Stage(10, "ltx-model")));
        ClipPlan clip = Assert.Single(plan.Clips);
        using ArchitectureRuntimeDispatcher dispatcher = new(
        [
            new ProjectingSession(
                new("ltx2"),
                context => ValidArtifact(context.Clip) with
                {
                    Video = new("invalid", 0, (DecodedMediaKind)rawVideoKind),
                    Width = width,
                    Height = height,
                    FramesPerSecond = framesPerSecond,
                    Frames = frames,
                }),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Execute(new ArchitectureClipRuntimeContext(
                clip,
                0,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)));

        Assert.Contains("invalid decoded media artifact", error.Message);
    }

    [Fact]
    public void Runtime_dispatcher_rejects_mistyped_attached_audio_artifact()
    {
        VideoExecutionPlan plan = Plan(GeneratedClip(7, Stage(10, "ltx-model")));
        ClipPlan clip = Assert.Single(plan.Clips);
        using ArchitectureRuntimeDispatcher dispatcher = new(
        [
            new ProjectingSession(
                new("ltx2"),
                context => ValidArtifact(context.Clip) with
                {
                    Audio = new("mistyped-audio", 0, DecodedMediaKind.Video),
                }),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Execute(new ArchitectureClipRuntimeContext(
                clip,
                0,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)));

        Assert.Contains("invalid decoded media artifact", error.Message);
    }

    [Fact]
    public void Runtime_dispatcher_rejects_null_session_result()
    {
        VideoExecutionPlan plan = Plan(GeneratedClip(7, Stage(10, "ltx-model")));
        ClipPlan clip = Assert.Single(plan.Clips);
        using ArchitectureRuntimeDispatcher dispatcher = new(
        [
            new ProjectingSession(new("ltx2"), _ => null),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Execute(new ArchitectureClipRuntimeContext(
                clip,
                0,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)));

        Assert.Contains("returned no decoded clip artifact", error.Message);
    }

    [Fact]
    public void Source_only_session_has_no_ltx_runtime_dependency()
    {
        Assert.DoesNotContain(
            typeof(SourceOnlyGenerationSession).GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(StageClipExecutor)
                || parameter.ParameterType.Namespace?.Contains("Ltx", StringComparison.OrdinalIgnoreCase)
                    == true);
    }

    [Fact]
    public void Runtime_registry_builds_an_injected_architecture_session()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            AuthoredArchitectureHint = "fake",
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };
        VideoStagesSpec spec = Spec(clip);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry()));
        List<string> calls = [];
        ArchitectureRuntimeSessionFactoryRegistry runtimes = new(
        [
            new RecordingSessionFactory(new("fake"), calls),
        ],
        new VideoExecutionPlanContext(plan));
        AudioRuntimeSources audio = new(
            null,
            new Dictionary<int, SwarmUI.Builtin_ComfyUIBackend.WGNodeData>(),
            new Dictionary<int, SwarmUI.Builtin_ComfyUIBackend.WGNodeData>());
        RootExecutionPolicy policy = new(plan);
        runtimes.PrepareTimeline(new(plan, audio, policy));
        using ArchitectureRuntimeDispatcher dispatcher = runtimes.CreateDispatcher(new(
            plan,
            audio,
            policy,
            Assembly: null));

        _ = dispatcher.Execute(new ArchitectureClipRuntimeContext(
            plan.Clips[0],
            0,
            PreviousClip: null,
            PreviousClipOutput: null,
            PreviousTimelineClipOutput: null));

        Assert.Equal(["fake"], calls);
    }

    [Fact]
    public void Production_manifest_keeps_module_and_runtime_registration_in_lockstep()
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            Workflow = [],
        };

        Assert.Equal(
            VideoArchitectureManifest.Production.Select(item => item.Module.Descriptor.Id),
            VideoArchitectureManifest.CreateProductionRuntimeProviders(
                generator,
                VideoArchitectureManifest.Production.Select(
                    item => item.Module.Descriptor.Id))
                .Select(provider => provider.ArchitectureId));
    }

    [Fact]
    public void Production_manifest_constructs_only_requested_runtime_providers_in_request_order()
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            Workflow = [],
        };

        IReadOnlyList<IArchitectureGenerationSessionFactoryProvider> providers =
            VideoArchitectureManifest.CreateProductionRuntimeProviders(
                generator,
                [new("wan22"), new("none"), new("wan22")]);

        Assert.Equal(
            [new ArchitectureId("wan22"), new ArchitectureId("none")],
            providers.Select(provider => provider.ArchitectureId));
    }

    [Fact]
    public void Production_manifest_rejects_a_missing_runtime_provider_binding()
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            Workflow = [],
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            VideoArchitectureManifest.CreateProductionRuntimeProviders(
                generator,
                [new("not-registered")]));

        Assert.Contains("not-registered", error.Message);
    }

    [Theory]
    [InlineData("ltx2,fake,ltx2,fake")]
    [InlineData("fake,ltx2,fake,ltx2")]
    public void Common_coordinator_dispatches_alternating_generated_architectures_and_assembles(
        string architectureOrder)
    {
        string[] ids = architectureOrder.Split(',');
        ClipSpec[] clips = [
            .. ids.Select((id, index) => GeneratedClip(
                index,
                Stage(index, id == "ltx2" ? "ltx-model" : "fake-model")) with
            {
                AuthoredArchitectureHint = id,
                AuthoredStages =
                [
                    new(
                        index,
                        id == "ltx2" ? "ltx-model" : "fake-model",
                        id == "ltx2" ? "ltx-profile" : "fake-profile",
                        false),
                ],
                BoundaryOut = Constants.BoundaryOutCut,
            })
        ];
        VideoStagesSpec spec = Spec(clips);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry()));
        // Synthetic alternating clips bypass production entry-role validation so this test can
        // isolate runtime routing.
        plan = plan with { Diagnostics = [] };

        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            bridge.AddStub("UnitTest_RootVideo", "root").WithOutputs(WGNodeData.DT_IMAGE);
            foreach (ClipPlan clip in plan.Clips)
            {
                bridge.AddStub(
                    "UnitTest_ArchitectureVideo",
                    $"{clip.Architecture.Id}-{clip.ClipId}")
                    .WithOutputs(WGNodeData.DT_IMAGE);
            }
        }
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.DoNotSave, true);
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            Workflow = workflow,
        };
        generator.CurrentMedia = new(
            new JArray("root", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Width = 512,
            Height = 512,
            Frames = 25,
            FPS = 24,
        };

        List<string> calls = [];
        List<ArchitectureClipRuntimeContext> runtimeContexts = [];
        VideoArchitectureExecutionHost host = new(
            generator,
            plan,
            [
                new RecordingSessionFactoryProvider(
                    new("ltx2"),
                    calls,
                    runtimeContexts),
                new RecordingSessionFactoryProvider(
                    new("fake"),
                    calls,
                    runtimeContexts),
            ]);
        VideoExecutionPlanContext request = new(plan, _ => host);
        request.PrepareRequest();

        host.RunConfiguredStages();

        Assert.Equal(ids, calls);
        Assert.All(runtimeContexts.Skip(1), context =>
        {
            Assert.NotNull(context.PreviousTimelineClipOutput);
            Assert.Null(context.PreviousClip);
            Assert.Null(context.PreviousClipOutput);
        });
        Assert.Equal(100, generator.CurrentMedia.Frames);
        Assert.Equal(VideoExecutionState.Completed, request.State);
        using WorkflowBridge result = WorkflowBridge.Create(workflow);
        Assert.Single(result.Graph.NodesOfType<BatchImagesNodeNode>());
    }

    [Fact]
    public void Runtime_dispatcher_disposes_partial_sessions_when_construction_fails()
    {
        TrackingSession first = new(new("first"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new ArchitectureRuntimeDispatcher(FailingSessions()));

        Assert.Equal("session construction failed", error.Message);
        Assert.True(first.Disposed);

        IEnumerable<IVideoGenerationSession> FailingSessions()
        {
            yield return first;
            throw new InvalidOperationException("session construction failed");
        }
    }

    [Fact]
    public void Registry_rejects_duplicate_architecture_ids()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureRegistry(
                [Ltx2ArchitectureModule.Instance, Ltx2ArchitectureModule.Instance]));
        Assert.Contains("Duplicate video architecture ids", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Registry_rejects_a_nonpositive_architecture_frame_grid(int frameGrid)
    {
        VideoArchitectureDescriptor descriptor = Descriptor("fake") with
        {
            FrameGrid = frameGrid,
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new VideoArchitectureRegistry([new MatchingModule(descriptor)]));

        Assert.Contains("invalid frame grid", error.Message);
    }

    [Fact]
    public void Registry_rejects_duplicate_entry_modes()
    {
        VideoArchitectureDescriptor invalid = Descriptor("fake") with
        {
            EntryModes =
            [
                ArchitectureEntryMode.ImageToVideo,
                ArchitectureEntryMode.ImageToVideo,
            ],
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureRegistry([new MatchingModule(invalid)]));

        Assert.Contains("missing, duplicate, or invalid entry modes", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registry_rejects_missing_or_invalid_entry_modes(
        bool invalidEnum)
    {
        IReadOnlyList<ArchitectureEntryMode> entryModes = invalidEnum
            ? [(ArchitectureEntryMode)int.MaxValue]
            : [];
        VideoArchitectureDescriptor invalid = Descriptor("fake") with
        {
            EntryModes = entryModes,
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureRegistry([new MatchingModule(invalid)]));

        Assert.Contains("missing, duplicate, or invalid entry modes", error.Message);
    }

    [Fact]
    public void Registry_rejects_incomplete_boundary_rule_catalog()
    {
        VideoArchitectureDescriptor invalid = Descriptor("fake") with
        {
            BoundaryPolicy = new ArchitectureBoundaryPolicy(
                new Dictionary<BoundaryJoinType, RuleDecision>
                {
                    [BoundaryJoinType.Cut] = RuleDecision.Supported(
                        "fake.cut",
                        "cut"),
                }),
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureRegistry([new MatchingModule(invalid)]));

        Assert.Contains("missing boundary rules", error.Message);
        Assert.Contains("Continue", error.Message);
        Assert.Contains("Crossfade", error.Message);
    }

    [Fact]
    public void Registry_rejects_ambiguous_model_matches()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        VideoArchitectureRegistry registry = new(
        [
            new MatchingModule(Descriptor("one")),
            new MatchingModule(Descriptor("two")),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => registry.TryResolveModel(models.VideoModel, out ResolvedVideoModel _));
        Assert.Contains("ambiguously resolves", error.Message);
    }

    [Fact]
    public void Registry_canonicalizes_model_resolution_to_the_registered_descriptor()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        VideoArchitectureDescriptor registered = Descriptor("fake");
        VideoArchitectureDescriptor divergent = registered with
        {
            DisplayName = "divergent same-id descriptor",
        };
        VideoArchitectureRegistry registry = new(
            [new MatchingModule(registered, divergent)]);

        Assert.True(registry.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved));

        Assert.Same(registered, resolved.Architecture);
        Assert.NotSame(divergent, resolved.Architecture);
    }

    [Fact]
    public async Task Catalog_api_matches_the_frontend_dto_and_lists_resolved_models()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject catalog = await VideoStagesApi.VideoStagesGetArchitectureCatalog(null);

        Assert.Equal(2, catalog.Value<int>("schemaVersion"));
        JArray architectures = (JArray)catalog["architectures"];
        Assert.Equal(
            ["none", "ltx2", "wan22", "host-video"],
            architectures.Values<JObject>().Select(item => item["id"]?.ToString()));
        JObject none = Assert.Single(
            architectures.Values<JObject>(),
            item => item["id"]?.ToString() == "none");
        Assert.Null(none["defaultProfileId"]);
        Assert.Null(none["profiles"]);
        Assert.Null(none["extras"]);
        Assert.Equal(
            ["audioSegments"],
            none["capabilities"]["features"].Values<string>());
        Assert.Equal(
            ["Disabled", "Upload"],
            none["capabilities"]["audioSourceKinds"].Values<string>());
        Assert.Null(none["capabilities"]["output"]);
        JObject ltx = Assert.Single(
            architectures.Values<JObject>(),
            item => item["id"]?.ToString() == "ltx2");
        Assert.Null(ltx["ignoredUnsupportedFeatures"]);
        Assert.Equal("ltx2", ltx["id"]);
        Assert.Equal("LTX Video 2.3", ltx["label"]);
        JObject capabilities = (JObject)ltx["capabilities"];
        Assert.Null(capabilities["modelProfiles"]);
        Assert.Null(capabilities["boundaryModes"]);
        Assert.Null(capabilities["conditionalRules"]);
        Assert.Null(capabilities["clipAudio"]);
        Assert.Equal(
            [
                "promptRelay",
                "frameReferences",
                "referenceFraming",
                "retake",
                "audioSegments",
                "audioReuse",
                "audioDerivedDuration",
                "icLora",
            ],
            capabilities["features"].Values<string>());
        Assert.Null(capabilities["initVideo"]);
        JObject crossfadeRule = (JObject)ltx["boundaryRules"]["crossfade"];
        Assert.Null(crossfadeRule["scope"]);
        Assert.Equal("conditional", crossfadeRule["support"]);
        Assert.Equal("ltx2.boundary.crossfade", crossfadeRule["code"]);
        Assert.Null(crossfadeRule["entityId"]);
        Assert.True(crossfadeRule["constraints"]["sameArchitecture"].Value<bool>());
        Assert.Equal(8, crossfadeRule["constraints"]["frameStep"]);
        Assert.Equal(8, crossfadeRule["constraints"]["minFrames"]);
        Assert.Equal(48, crossfadeRule["constraints"]["maxFrames"]);
        Assert.Equal(8, crossfadeRule["constraints"]["defaultFrames"]);
        JObject continueRule = (JObject)ltx["boundaryRules"]["continue"];
        Assert.Equal(1, continueRule["constraints"]["continuityExtraFrames"]);
        Assert.True(continueRule["constraints"]["targetRequiresGeneratedEntry"].Value<bool>());
        Assert.Null(ltx["rules"]);
        JObject wan = Assert.Single(
            architectures.Values<JObject>(),
            item => item["id"]?.ToString() == "wan22");
        Assert.Null(wan["ignoredUnsupportedFeatures"]);
        Assert.Equal(
            ["text-to-video", "image-to-video", "init-video"],
            wan["capabilities"]["entryModes"].Values<string>());
        JObject model = Assert.Single(
            ((JArray)catalog["models"]).Values<JObject>(),
            item => item["modelName"]?.ToString() == models.VideoModel.Name);
        Assert.Equal("ltx2", model["architectureId"]);
        Assert.Equal("ltx-2.3", model["modelProfileId"]);
        Assert.Equal(Ltx2ArchitectureModule.FrameGrid, model["frameGrid"]);
        Assert.Equal(
            capabilities["features"].Values<string>(),
            model["capabilities"]["features"].Values<string>());
        Assert.Null(model["entryModes"]);
        Assert.Null(model["enhancements"]["extras"]);
    }

    [Fact]
    public async Task Catalog_api_hides_models_forbidden_to_the_request_session()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject catalog = await VideoStagesApi.VideoStagesGetArchitectureCatalog(
            RestrictedSession(models.VideoModel.Name));

        Assert.DoesNotContain(
            ((JArray)catalog["models"]).Values<JObject>(),
            model => model["modelName"]?.ToString() == models.VideoModel.Name);
    }

    [Fact]
    public void Decoded_clip_boundary_exposes_no_vae_or_latent_property()
    {
        Assert.Null(typeof(DecodedClipArtifact).GetProperty("Vae"));
        Assert.DoesNotContain(
            typeof(DecodedClipArtifact).GetProperties(),
            property => property.Name.Contains("Latent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(DecodedClipArtifact).GetProperties(),
            property => property.PropertyType.Name.Contains(
                "MediaRef",
                StringComparison.OrdinalIgnoreCase));
    }

    private static VideoStagesSpec Spec(params ClipSpec[] clips) =>
        new(512, 512, 24, false, clips);

    private static ClipSpec GeneratedClip(int id, params StageSpec[] stages) =>
        new(
            id,
            25,
            Constants.AudioSourceNative,
            [],
            false,
            false,
            false,
            false,
            null,
            [],
            stages);

    private static ClipSpec InitVideoClip(int id) =>
        new(
            id,
            25,
            Constants.AudioSourceNative,
            [],
            false,
            false,
            false,
            false,
            null,
            [],
            [],
            InitVideo: new("data", "source.mp4", 0));

    private static StageSpec Stage(int id, string model) =>
        new(id, 1, 1, "pixel-lanczos", model, 12, 4.5, "euler", "normal", "Generated");

    private static VideoExecutionPlan Compile(ClipSpec clip, FakeRegistry registry)
    {
        VideoStagesSpec spec = Spec(clip);
        return VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ArchitecturePlanResolver.Resolve(spec, registry));
    }

    private static VideoArchitectureDescriptor FakeCapabilityDescriptor(
        IReadOnlyList<ArchitectureEntryMode> entryModes = null) =>
        Descriptor("fake") with
        {
            AudioSourceKinds = [AudioSourceKind.Native],
            EntryModes = entryModes ?? [ArchitectureEntryMode.ImageToVideo],
        };

    private static Session RestrictedSession(params string[] modelPrefixes)
    {
        Role role = new("video-stages-test")
        {
            Data = new Role.RoleData
            {
                ModelBlacklist = [.. modelPrefixes],
            },
        };
        User user = (User)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(User));
        user.Data = new User.DatabaseEntry { ID = "video-stages-test-user" };
        user.CalculatedRole = role;
        return new Session { User = user };
    }

    private static VideoExecutionPlan Plan(ClipSpec clip)
    {
        VideoStagesSpec spec = Spec(clip with
        {
            AuthoredArchitectureHint = "ltx2",
            AuthoredModelProfileHint = "ltx-profile",
            AuthoredStages =
            [
                new(
                    clip.Stages[0].ClipStageRawIndex,
                    "ltx-model",
                    "ltx-profile",
                    false),
            ],
        });
        return VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry()));
    }

    private static DecodedClipArtifact ValidArtifact(ClipPlan clip) =>
        new(
            new("video", 0, DecodedMediaKind.Video),
            null,
            512,
            512,
            24,
            25,
            clip.Architecture.Id,
            clip.ClipId);

    private sealed class FakeRegistry : IVideoArchitectureRegistry
    {
        private readonly Dictionary<string, ResolvedVideoModel> _models;
        private readonly Dictionary<ArchitectureId, IVideoArchitectureModule> _modules;

        internal FakeRegistry(
            bool undeclaredFakeProfile = false,
            VideoArchitectureDescriptor fakeDescriptor = null)
        {
            VideoArchitectureDescriptor ltx = Descriptor("ltx2") with
            {
                EntryModes =
                [
                    ArchitectureEntryMode.TextToVideo,
                    ArchitectureEntryMode.ImageToVideo,
                ],
            };
            VideoArchitectureDescriptor fake =
                fakeDescriptor ?? Descriptor("fake");
            Catalog = [ltx, fake];
            _models = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ltx-model"] = Resolved("ltx-model", ltx, "ltx-profile"),
                ["ltx23-model"] = Resolved("ltx23-model", ltx, "ltx-2.3-profile"),
                ["fake-model"] = Resolved(
                    "fake-model",
                    fake,
                    undeclaredFakeProfile ? "ghost-profile" : "fake-profile"),
            };
            CompileCounts = new()
            {
                [ltx.Id] = 0,
                [fake.Id] = 0,
            };
            _modules = new()
            {
                [ltx.Id] = new FakeModule(ltx, () => CompileCounts[ltx.Id]++),
                [fake.Id] = new FakeModule(fake, () => CompileCounts[fake.Id]++),
            };
            ResolvedModels = [.. _models.Values];
        }

        public IReadOnlyList<VideoArchitectureDescriptor> Catalog { get; }

        public IReadOnlyList<ResolvedVideoModel> ResolvedModels { get; }

        internal Dictionary<ArchitectureId, int> CompileCounts { get; }

        public IVideoArchitectureModule GetModule(ArchitectureId architectureId) =>
            _modules[architectureId];

        public bool TryResolveModel(string modelName, out ResolvedVideoModel resolved) =>
            _models.TryGetValue(modelName ?? "", out resolved);

        public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved) =>
            TryResolveModel(model?.Name, out resolved);

        private static ResolvedVideoModel Resolved(
            string name,
            VideoArchitectureDescriptor architecture,
            string profile) =>
            TestResolvedVideoModel.Create(
                name,
                new(profile),
                architecture,
                $"{architecture.Id}-test-model",
                $"{architecture.Id}-test-compat");

        private sealed class FakeModule(
            VideoArchitectureDescriptor descriptor,
            Action compiled) : IVideoArchitectureModule
        {
            public VideoArchitectureDescriptor Descriptor => descriptor;

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
                compiled();
                return new(
                    new FakeClipPayload(descriptor.Id),
                    (clip.Stages ?? []).ToDictionary(
                        stage => stage.ClipStageRawIndex,
                        _ => (IArchitectureStagePayload)new FakeStagePayload(descriptor.Id)),
                    []);
            }
        }

        private sealed record FakeClipPayload(
            ArchitectureId ArchitectureId) : IArchitectureClipPayload;
    }

    private static VideoArchitectureDescriptor Descriptor(string id) =>
        new(
            new(id),
            id,
            [AudioSourceKind.Native],
            [
                ArchitectureEntryMode.TextToVideo,
                ArchitectureEntryMode.ImageToVideo,
            ],
            ArchitectureFeature.None,
            new ArchitectureBoundaryPolicy(
                new Dictionary<BoundaryJoinType, RuleDecision>
                {
                    [BoundaryJoinType.Cut] = RuleDecision.Supported(
                        $"{id}.cut",
                        "cut"),
                    [BoundaryJoinType.Continue] = BoundaryMode(
                        $"{id}.continue",
                        "same architecture"),
                    [BoundaryJoinType.Crossfade] = BoundaryMode(
                        $"{id}.crossfade",
                        "same architecture"),
                }));

    private static RuleDecision BoundaryMode(
        string code,
        string reason) =>
        RuleDecision.Conditional(
            code,
            reason,
            new BoundaryRuleConstraints(
                FrameStep: 1,
                MinFrames: 1,
                MaxFrames: 8,
                DefaultFrames: 1,
                ContinuityExtraFrames: 0,
                TargetRequiresGeneratedEntry: false,
                TargetRequiresStage: false,
                TargetDisallowsInitialReference: false));

    private sealed class MatchingModule(
        VideoArchitectureDescriptor descriptor,
        VideoArchitectureDescriptor resolvedDescriptor = null)
        : IVideoArchitectureModule
    {
        public VideoArchitectureDescriptor Descriptor => descriptor;

        public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
        {
            resolved = TestResolvedVideoModel.Create(
                model.Name,
                new($"{descriptor.Id.Value}-test-profile"),
                resolvedDescriptor ?? descriptor,
                model.ModelClass.ID,
                model.ModelClass.CompatClass.ID);
            return true;
        }

        public ArchitectureClipCompilation ValidateAndCompileClip(
            ClipSpec clip,
            IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
            ArchitectureClipCompileContext context) =>
            new(
                new FakePayload(descriptor.Id),
                (clip.Stages ?? []).ToDictionary(
                    stage => stage.ClipStageRawIndex,
                    _ => (IArchitectureStagePayload)new FakeStagePayload(descriptor.Id)),
                []);
    }

    private sealed record FakePayload(
        ArchitectureId ArchitectureId) : IArchitectureClipPayload;

    private sealed record FakeStagePayload(
        ArchitectureId ArchitectureId) : IArchitectureStagePayload
    {
        public StageCorePlan Core => TestPlanCompiler.DefaultStageCore;
    }

    private sealed class RecordingSession(
        ArchitectureId architectureId,
        ICollection<string> calls,
        ICollection<ArchitectureClipRuntimeContext> contexts = null) : IVideoGenerationSession
    {
        public ArchitectureId ArchitectureId => architectureId;

        public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
        {
            contexts?.Add(context);
            calls.Add(architectureId.Value);
            return new(
                new(
                    $"{architectureId}-{context.Clip.ClipId}",
                    0,
                    DecodedMediaKind.Video),
                null,
                512,
                512,
                24,
                25,
                architectureId,
                context.Clip.ClipId);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ProjectingSession(
        ArchitectureId architectureId,
        Func<ArchitectureClipRuntimeContext, DecodedClipArtifact> project)
        : IVideoGenerationSession
    {
        public ArchitectureId ArchitectureId => architectureId;

        public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context) =>
            project(context);

        public void Dispose()
        {
        }
    }

    private sealed class TrackingSession(ArchitectureId architectureId) :
        IVideoGenerationSession
    {
        public ArchitectureId ArchitectureId => architectureId;

        internal bool Disposed { get; private set; }

        public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context) =>
            throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingSessionFactory(
        ArchitectureId architectureId,
        ICollection<string> calls,
        ICollection<ArchitectureClipRuntimeContext> contexts = null)
        : IArchitectureGenerationSessionFactory
    {
        public ArchitectureId ArchitectureId => architectureId;

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context) =>
            new RecordingSession(architectureId, calls, contexts);
    }

    private sealed class RecordingSessionFactoryProvider(
        ArchitectureId architectureId,
        ICollection<string> calls,
        ICollection<ArchitectureClipRuntimeContext> contexts = null)
        : IArchitectureGenerationSessionFactoryProvider
    {
        public ArchitectureId ArchitectureId => architectureId;

        public IArchitectureGenerationSessionFactory CreateFactory() =>
            new RecordingSessionFactory(architectureId, calls, contexts);
    }

}
