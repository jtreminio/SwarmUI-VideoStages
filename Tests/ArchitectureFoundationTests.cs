using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
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
            AuthoredArchitectureId = "ltx2",
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
    public void Resolver_rejects_persisted_clip_identity_mismatch()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureId = "fake",
            AuthoredStages = [new(0, "ltx-model", "ltx-profile", Skipped: false)],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(clip), new FakeRegistry());

        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "architecture-authored-identity-mismatch");
    }

    [Fact]
    public void Resolver_rejects_a_model_forbidden_to_the_request_session()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureId = "ltx2",
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

        // Pinning the documented policy: no user means no authorization context to
        // apply, not an unauthenticated request to refuse.
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
            AuthoredArchitectureId = "ltx2",
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
    public void Resolver_validates_profile_ids_for_skipped_raw_stage_positions()
    {
        ClipSpec inactive = GeneratedClip(0) with
        {
            AuthoredArchitectureId = "ltx2",
            AuthoredModelProfileId = "ltx-profile",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
                new(3, "ltx-model", "wrong-profile", Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(inactive), new FakeRegistry());

        PlanDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "architecture-authored-stage-profile-mismatch");
        Assert.Contains("stage 3", diagnostic.Message);
        Assert.Equal(3, diagnostic.RawStageIndex);
        Assert.Equal(new ArchitectureId("ltx2"), result.Clips[0].Architecture.Id);
    }

    [Fact]
    public void Resolver_validates_all_skipped_sourced_chain_before_executing_as_none()
    {
        ClipSpec sourced = SourcedClip(0) with
        {
            AuthoredArchitectureId = "ltx2",
            AuthoredModelProfileId = "ltx-profile",
            AuthoredStages =
            [
                new(0, "ltx-model", "ltx-profile", Skipped: true),
                new(4, "fake-model", "fake-profile", Skipped: true),
            ],
        };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(sourced), new FakeRegistry());

        Assert.Equal(NoneArchitecture.Id, result.Clips[0].Architecture.Id);
        PlanDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "architecture-mixed-authored-stage-clip");
        Assert.Equal(4, diagnostic.RawStageIndex);
        Assert.Contains("stage 4 (skipped)", diagnostic.Message);
    }

    [Fact]
    public void Resolver_rejects_module_profile_not_declared_by_descriptor()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            AuthoredStages = [new(0, "fake-model", "ghost-profile", false)],
        };

        ArchitecturePlanningResult result = ArchitecturePlanResolver.Resolve(
            Spec(clip),
            new FakeRegistry(undeclaredFakeProfile: true));

        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "architecture-resolved-profile-not-declared");
    }

    [Fact]
    public void Source_only_clip_is_none_and_rejects_an_authored_generation_architecture()
    {
        ClipSpec clip = SourcedClip(0) with { AuthoredArchitectureId = "ltx2" };

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(Spec(clip), new FakeRegistry());

        Assert.Equal(NoneArchitecture.Id, result.Clips[0].Architecture.Id);
        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "architecture-source-only-identity-mismatch");
    }

    [Fact]
    public void Sourced_only_dormant_mixed_stages_report_only_the_dormant_architecture_error()
    {
        ClipSpec clip = SourcedClip(0) with
        {
            AuthoredArchitectureId = "none",
            AuthoredModelProfileId = "none",
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
    public void Sourced_only_same_architecture_dormant_stages_plan_as_none_without_identity_error()
    {
        ClipSpec clip = SourcedClip(0) with
        {
            AuthoredArchitectureId = "none",
            AuthoredModelProfileId = "none",
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
    public void Sourced_only_same_architecture_dormant_stages_may_use_different_profiles()
    {
        ClipSpec clip = SourcedClip(0) with
        {
            AuthoredArchitectureId = "none",
            AuthoredModelProfileId = "none",
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
    public void Cross_architecture_non_cut_boundary_is_a_blocking_plan_error()
    {
        ClipSpec left = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureId = "ltx2",
            AuthoredStages = [new(0, "ltx-model", "ltx-profile", false)],
        };
        left = left with { BoundaryOut = Constants.BoundaryOutCrossfade };
        ClipSpec right = GeneratedClip(1, Stage(11, "fake-model")) with
        {
            AuthoredArchitectureId = "fake",
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
        Assert.Equal(BoundaryExecutionMode.Crossfade, boundary.Requested);
        Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == "boundary-cross-architecture-non-cut"
                && item.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Plan_carries_clip_architecture_and_stage_profile_identities()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureId = "ltx2",
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
            AuthoredArchitectureId = "ltx2",
            AuthoredModelProfileId = "ltx-profile",
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
    public void Capability_validation_blocks_fake_module_before_it_receives_ltx_options()
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

        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == "architecture-capability-unsupported");
        Assert.Equal(0, registry.CompileCounts[new ArchitectureId("fake")]);
        Assert.Null(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Fact]
    public void Capability_validation_rejects_nondefault_reference_framing()
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
            item => item.Code == "architecture-capability-unsupported"
                && item.Message.Contains("reference framing"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_rejects_multiple_active_stages_before_fake_module()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor();
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(
            0,
            Stage(10, "fake-model") with { ClipStageRawIndex = 0 },
            Stage(11, "fake-model") with
            {
                ClipStageIndex = 1,
                ClipStageRawIndex = 1,
            }) with
        {
            AuthoredStages =
            [
                new(0, "fake-model", "fake-profile", false),
                new(1, "fake-model", "fake-profile", false),
            ],
        };
        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == "architecture-capability-unsupported"
                && item.Message.Contains("multiple active stages"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_does_not_count_skipped_dormant_stage_as_multistage()
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
            item => item.Message.Contains("multiple active stages"));
        Assert.Equal(1, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Source_only_none_ignores_dormant_generation_authoring_options()
    {
        ClipSpec clip = SourcedClip(0) with
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
            AuthoredArchitectureId = "none",
            AuthoredModelProfileId = "none",
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
    public void Source_only_none_rejects_captured_stage_audio_reuse()
    {
        ClipSpec clip = SourcedClip(0) with
        {
            ReuseAudio = true,
            AuthoredArchitectureId = "none",
            AuthoredModelProfileId = "none",
        };

        VideoExecutionPlan plan = Compile(clip, new FakeRegistry());

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Message.Contains("captured stage audio reuse"));
        Assert.Null(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    [Fact]
    public void Source_only_none_rejects_audio_derived_duration_but_accepts_uploaded_audio()
    {
        ClipSpec clip = SourcedClip(0) with
        {
            AudioSource = Constants.AudioSourceUpload,
            UploadedAudio = new("data:audio/wav;base64,AA==", "voice.wav"),
            ClipLengthFromAudio = true,
            AuthoredArchitectureId = "none",
            AuthoredModelProfileId = "none",
        };

        VideoExecutionPlan plan = Compile(clip, new FakeRegistry());

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Message.Contains("audio-derived clip duration"));
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Message.Contains("clip audio source"));
        Assert.Null(Assert.Single(plan.Clips).ArchitecturePayload);
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
        Dictionary<int, ResolvedVideoModel> stageModels = new()
        {
            [stage.ClipStageRawIndex] = new(
                stage.Model,
                descriptor.Id,
                descriptor.DefaultProfileId,
                descriptor),
        };

        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                clip,
                descriptor,
                ArchitectureEntryMode.ImageToVideo,
                stageModels);

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
        Dictionary<int, ResolvedVideoModel> stageModels = new()
        {
            [stage.ClipStageRawIndex] = new(
                stage.Model,
                descriptor.Id,
                descriptor.DefaultProfileId,
                descriptor),
        };

        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                clip,
                descriptor,
                ArchitectureEntryMode.ImageToVideo,
                stageModels);

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
            [stage.ClipStageRawIndex] = new(
                stage.Model,
                descriptor.Id,
                descriptor.DefaultProfileId,
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
    public void Capability_validation_requires_video_input_for_every_later_stage()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor(
            architecture: ArchitectureCapability.GeneratedEntry
                | ArchitectureCapability.MultiStage
                | ArchitectureCapability.DecodedOutput,
            stage: StageCapability.ImageInput);
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(
            0,
            Stage(10, "fake-model") with { ClipStageRawIndex = 0 },
            Stage(11, "fake-model") with
            {
                ClipStageIndex = 1,
                ClipStageRawIndex = 1,
            }) with
        {
            AuthoredStages =
            [
                new(0, "fake-model", "fake-profile", false),
                new(1, "fake-model", "fake-profile", false),
            ],
        };

        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.StageId == 11
                && diagnostic.Message.Contains("video stage input for a later stage"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_requires_stage_frame_reference_support()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor(
            stage: StageCapability.ImageInput | StageCapability.VideoInput) with
        {
            Capabilities = FakeCapabilityDescriptor().Capabilities with
            {
                Clip = ClipCapability.Prompts | ClipCapability.References,
            },
        };
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            ImageRefs = [new("Upload", 1, false, "ref.png")],
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };

        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Message.Contains("frame references"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Theory]
    [InlineData(
        (int)(ModelProfileCapability.SchedulerSelection
            | ModelProfileCapability.DimensionRules
            | ModelProfileCapability.FrameRules),
        "sampler selection")]
    [InlineData(
        (int)(ModelProfileCapability.SamplerSelection
            | ModelProfileCapability.DimensionRules
            | ModelProfileCapability.FrameRules),
        "scheduler selection")]
    [InlineData(
        (int)(ModelProfileCapability.SamplerSelection
            | ModelProfileCapability.SchedulerSelection
            | ModelProfileCapability.FrameRules),
        "dimension rules")]
    [InlineData(
        (int)(ModelProfileCapability.SamplerSelection
            | ModelProfileCapability.SchedulerSelection
            | ModelProfileCapability.DimensionRules),
        "frame rules")]
    public void Capability_validation_requires_exact_profile_execution_features(
        int supportedValue,
        string missingFeature)
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor(
            profile: (ModelProfileCapability)supportedValue);
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(0, Stage(10, "fake-model")) with
        {
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };

        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-capability-unsupported"
                && diagnostic.Message.Contains(missingFeature));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_rejects_lora_when_resolved_profile_lacks_lora()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor(
            stage: StageCapability.ImageInput
                | StageCapability.VideoInput
                | StageCapability.Lora);
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(
            0,
            Stage(10, "fake-model") with
            {
                Loras = [new("profile-blocked.safetensors")],
            }) with
        {
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };
        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            item => item.Message.Contains(
                "normal LoRA for model profile 'fake-profile'"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_rejects_normal_lora_when_profile_omits_normal_lora()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor(
            stage: StageCapability.ImageInput
                | StageCapability.VideoInput
                | StageCapability.Lora,
            profile: ModelProfileCapability.SamplerSelection);
        FakeRegistry registry = new(fakeDescriptor: descriptor);
        ClipSpec clip = GeneratedClip(
            0,
            Stage(10, "fake-model") with
            {
                Loras = [new("normal-authored.safetensors")],
            }) with
        {
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };

        VideoExecutionPlan plan = Compile(clip, registry);

        Assert.Contains(
            plan.Diagnostics,
            item => item.Message.Contains(
                "normal LoRA for model profile 'fake-profile'"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
    }

    [Fact]
    public void Capability_validation_rejects_the_actual_unsupported_upscale_mode()
    {
        VideoArchitectureDescriptor descriptor = FakeCapabilityDescriptor(
            stage: StageCapability.ImageInput
                | StageCapability.VideoInput
                | StageCapability.PixelUpscale);
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

        Assert.Contains(
            plan.Diagnostics,
            item => item.Message.Contains("upscale mode 'ModelUpscale'"));
        Assert.Equal(0, registry.CompileCounts[new("fake")]);
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
            item => item.Message.Contains("entry mode 'ImageToVideo'"));
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
                AuthoredArchitectureId = id,
                AuthoredModelProfileId = $"{(id == "ltx2" ? "ltx" : "fake")}-profile",
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
            Assert.Equal(BoundaryExecutionMode.Cut, boundary.Requested);
            Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        });
        Assert.Equal(ids, calls);
        Assert.Equal(
            ids,
            outputs.Select(output => output.ArchitectureId.Value).ToArray());
    }

    [Fact]
    public void Runtime_dispatcher_rejects_mismatched_session_result_identity()
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
                    ClipId = 88,
                }),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Execute(new ArchitectureClipRuntimeContext(
                clip,
                0,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)));

        Assert.Contains("artifact identity 'fake/88'", error.Message);
        Assert.Contains("planned clip 'ltx2/7'", error.Message);
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
            AuthoredArchitectureId = "fake",
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
        ]);
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
    public void Runtime_registry_rejects_multiple_active_whole_timeline_finalizers()
    {
        ClipSpec first = GeneratedClip(0, Stage(10, "ltx-model")) with
        {
            AuthoredArchitectureId = "ltx2",
            AuthoredStages = [new(0, "ltx-model", "ltx-profile", false)],
            BoundaryOut = Constants.BoundaryOutCut,
        };
        ClipSpec second = GeneratedClip(1, Stage(11, "fake-model")) with
        {
            AuthoredArchitectureId = "fake",
            AuthoredStages = [new(0, "fake-model", "fake-profile", false)],
        };
        VideoStagesSpec spec = Spec(first, second);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ArchitecturePlanResolver.Resolve(spec, new FakeRegistry()));
        List<string> calls = [];
        ArchitectureRuntimeSessionFactoryRegistry runtimes = new(
        [
            new ExclusiveFinalizerFactory(new("ltx2"), calls),
            new ExclusiveFinalizerFactory(new("fake"), calls),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            runtimes.FinalizeTimeline(new(plan, Publication: null)));

        Assert.Contains("Multiple architectures", error.Message);
        Assert.Empty(calls);
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
            VideoArchitectureManifest.CreateProductionRuntimeProviders(generator)
                .Select(provider => provider.ArchitectureId));
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
                AuthoredArchitectureId = id,
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

        host.RunConfiguredStages(new VideoExecutionPlanContext(plan));

        Assert.Equal(ids, calls);
        Assert.All(runtimeContexts.Skip(1), context =>
        {
            Assert.NotNull(context.PreviousTimelineClipOutput);
            Assert.Null(context.PreviousClip);
            Assert.Null(context.PreviousClipOutput);
        });
        Assert.Equal(100, generator.CurrentMedia.Frames);
        using WorkflowBridge result = WorkflowBridge.Create(workflow);
        Assert.Single(result.Graph.NodesOfType<BatchImagesNodeNode>());
    }

    [Fact]
    public void Registry_rejects_duplicate_architecture_ids()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureRegistry(
                [Ltx2ArchitectureModule.Instance, Ltx2ArchitectureModule.Instance]));
        Assert.Contains("Duplicate video architecture ids", error.Message);
    }

    [Fact]
    public void Registry_rejects_default_profile_outside_descriptor_catalog()
    {
        VideoArchitectureDescriptor invalid = Descriptor("fake", "declared") with
        {
            DefaultProfileId = new("missing"),
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureRegistry([new MatchingModule(invalid)]));

        Assert.Contains("default profile 'missing' is not declared", error.Message);
    }

    [Fact]
    public void Registry_rejects_duplicate_profile_ids()
    {
        VideoArchitectureDescriptor invalid = Descriptor("fake", "profile") with
        {
            Profiles =
            [
                new(new("profile"), "first", ModelProfileCapability.None, []),
                new(new("profile"), "second", ModelProfileCapability.None, []),
            ],
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new VideoArchitectureRegistry([new MatchingModule(invalid)]));

        Assert.Contains("duplicate model profile ids", error.Message);
    }

    [Fact]
    public void Registry_rejects_incomplete_boundary_rule_catalog()
    {
        VideoArchitectureDescriptor invalid = Descriptor("fake", "profile") with
        {
            BoundaryPolicy = new ArchitectureBoundaryPolicy(
                new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
                {
                    [BoundaryExecutionMode.Cut] = BoundaryMode(
                        RuleSupport.Supported,
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
            new MatchingModule(Descriptor("one", "one-profile")),
            new MatchingModule(Descriptor("two", "two-profile")),
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
        VideoArchitectureDescriptor registered = Descriptor("fake", "profile");
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

        JArray architectures = (JArray)catalog["architectures"];
        Assert.Equal(
            ["none", "ltx2", "wan22"],
            architectures.Values<JObject>().Select(item => item["id"]?.ToString()));
        JObject none = Assert.Single(
            architectures.Values<JObject>(),
            item => item["id"]?.ToString() == "none");
        Assert.Equal("none", none["defaultProfileId"]);
        Assert.Equal(
            ["sourced-entry", "decoded-output"],
            none["capabilities"]["architecture"].Values<string>());
        Assert.Equal(
            ["source-video", "audio-sources", "audio-segments"],
            none["capabilities"]["clip"].Values<string>());
        Assert.Equal(
            ["Disabled", "Upload"],
            none["capabilities"]["audioSourceKinds"].Values<string>());
        Assert.Empty(none["capabilities"]["stage"]);
        Assert.Equal(
            ["video", "attached-audio"],
            none["capabilities"]["output"].Values<string>());
        Assert.Empty(none["capabilities"]["upscaleModes"]);
        Assert.Equal(
            "none",
            Assert.Single((JArray)none["profiles"])["id"]);

        JObject ltx = Assert.Single(
            architectures.Values<JObject>(),
            item => item["id"]?.ToString() == "ltx2");
        Assert.Equal("ltx2", ltx["id"]);
        Assert.Equal("LTX Video 2.3", ltx["label"]);
        Assert.Equal("ltx-2.3", ltx["defaultProfileId"]);
        JObject capabilities = (JObject)ltx["capabilities"];
        Assert.Null(capabilities["modelProfiles"]);
        Assert.Null(capabilities["boundaryModes"]);
        Assert.Null(capabilities["conditionalRules"]);
        Assert.Null(capabilities["clipAudio"]);
        Assert.Equal(
            [
                "generated-entry",
                "sourced-entry",
                "multi-stage",
                "native-audio",
                "decoded-output",
            ],
            capabilities["architecture"].Values<string>());
        Assert.Equal(
            [
                "source-video",
                "prompts",
                "prompt-relay",
                "references",
                "reference-framing",
                "retake",
                "audio-sources",
                "audio-segments",
                "audio-reuse",
                "audio-derived-duration",
            ],
            capabilities["clip"].Values<string>());
        Assert.Contains("frame-references", capabilities["stage"].Values<string>());
        Assert.Equal(
            ["pixel", "model", "latent", "latent-model"],
            capabilities["upscaleModes"].Values<string>());
        Assert.Contains("standalone-audio", capabilities["output"].Values<string>());
        Assert.Null(capabilities["sourceVideo"]);
        JObject crossfadeRule = (JObject)ltx["boundaryRules"]["crossfade"];
        Assert.Equal("boundary", crossfadeRule["scope"]);
        Assert.Equal("conditional", crossfadeRule["support"]);
        Assert.Equal("ltx2.boundary.crossfade", crossfadeRule["code"]);
        Assert.Null(crossfadeRule["entityId"].Value<string>());
        Assert.True(crossfadeRule["constraints"]["sameArchitecture"].Value<bool>());
        Assert.Equal(8, crossfadeRule["constraints"]["frameStep"]);
        Assert.Equal(8, crossfadeRule["constraints"]["minFrames"]);
        Assert.Equal(48, crossfadeRule["constraints"]["maxFrames"]);
        Assert.Equal(8, crossfadeRule["constraints"]["defaultFrames"]);
        JObject continueRule = (JObject)ltx["boundaryRules"]["continue"];
        Assert.Equal(1, continueRule["constraints"]["continuityExtraFrames"]);
        Assert.True(continueRule["constraints"]["targetRequiresGeneratedEntry"].Value<bool>());
        JObject profile = Assert.Single(
            ((JArray)ltx["profiles"]).Values<JObject>(),
            item => item["id"]?.ToString() == "ltx-2.3");
        Assert.Contains("frame-rules", profile["capabilities"].Values<string>());
        Assert.NotNull(profile["rules"]);
        JArray rules = (JArray)ltx["rules"];
        Assert.Contains(
            rules.Values<JObject>(),
            rule => rule["code"]?.ToString() == "retake-frame-references-unsupported"
                && rule["scope"]?.ToString() == "stage");
        Assert.Contains(
            rules.Values<JObject>(),
            rule => rule["code"]?.ToString() == "mixed-hdr-timeline-unsupported"
                && rule["constraints"]?["uniformTimelineFeature"]?.ToString() == "hdr");
        Assert.Contains(
            rules.Values<JObject>(),
            rule => rule["code"]?.ToString() == "audio.reuse.requires_three_stages"
                && rule["constraints"]?["minimumActiveStages"]?.Value<int>() == 3
                && rule["constraints"]?["failureSeverity"]?.ToString() == "warning"
                && rule["constraints"]?["failureEffect"]?.ToString() == "disable-feature");
        JObject model = Assert.Single(
            ((JArray)catalog["models"]).Values<JObject>(),
            item => item["modelName"]?.ToString() == models.VideoModel.Name);
        Assert.Equal("ltx2", model["architectureId"]);
        Assert.Equal("ltx-2.3", model["modelProfileId"]);
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

    private static ClipSpec SourcedClip(int id) =>
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
            SourceVideo: new("data", "source.mp4", 0));

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
        ArchitectureCapability architecture =
            ArchitectureCapability.GeneratedEntry | ArchitectureCapability.DecodedOutput,
        StageCapability stage =
            StageCapability.ImageInput | StageCapability.VideoInput,
        ModelProfileCapability profile =
            ModelProfileCapability.SamplerSelection
                | ModelProfileCapability.SchedulerSelection
                | ModelProfileCapability.DimensionRules
                | ModelProfileCapability.FrameRules,
        IReadOnlyList<ArchitectureEntryMode> entryModes = null) =>
        Descriptor("fake", "fake-profile") with
        {
            EntryModes = entryModes ?? [ArchitectureEntryMode.ImageToVideo],
            AudioSourceKinds = [AudioSourceKind.Native],
            Profiles =
            [
                new(
                    new("fake-profile"),
                    "fake-profile",
                    profile,
                    []),
            ],
            Capabilities = new(
                architecture,
                ClipCapability.Prompts,
                stage,
                OutputCapability.Video),
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
            AuthoredArchitectureId = "ltx2",
            AuthoredModelProfileId = "ltx-profile",
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
            VideoArchitectureDescriptor ltx = Descriptor("ltx2", "ltx-profile") with
            {
                Profiles =
                [
                    new(
                        new("ltx-profile"),
                        "ltx-profile",
                        ModelProfileCapability.SamplerSelection
                            | ModelProfileCapability.SchedulerSelection
                            | ModelProfileCapability.DimensionRules
                            | ModelProfileCapability.FrameRules,
                        []),
                    new(
                        new("ltx-2.3-profile"),
                        "ltx-2.3-profile",
                        ModelProfileCapability.SamplerSelection
                            | ModelProfileCapability.SchedulerSelection
                            | ModelProfileCapability.DimensionRules
                            | ModelProfileCapability.FrameRules,
                        []),
                ],
            };
            VideoArchitectureDescriptor fake =
                fakeDescriptor ?? Descriptor("fake", "fake-profile");
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
            new(name, architecture.Id, new(profile), architecture);

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
                return new(new FakeClipPayload(descriptor.Id), []);
            }
        }

        private sealed record FakeClipPayload(
            ArchitectureId ArchitectureId) :
            IArchitectureClipPayload,
            IArchitectureStagePayloadSource
        {
            public IArchitectureStagePayload GetStagePayload(int rawStageIndex) =>
                new FakeStagePayload(ArchitectureId);
        }
    }

    private static VideoArchitectureDescriptor Descriptor(string id, string profile) =>
        new(
            new(id),
            id,
            new(profile),
            [
                ArchitectureEntryMode.TextToVideo,
                ArchitectureEntryMode.ImageToVideo,
            ],
            [AudioSourceKind.Native],
            [new(
                new(profile),
                profile,
                ModelProfileCapability.SamplerSelection
                    | ModelProfileCapability.SchedulerSelection
                    | ModelProfileCapability.DimensionRules
                    | ModelProfileCapability.FrameRules,
                [])],
            new(
                ArchitectureCapability.GeneratedEntry | ArchitectureCapability.DecodedOutput,
                ClipCapability.Prompts,
                StageCapability.ImageInput | StageCapability.VideoInput,
                OutputCapability.Video),
            new ArchitectureBoundaryPolicy(
                new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
                {
                    [BoundaryExecutionMode.Cut] = BoundaryMode(
                        RuleSupport.Supported,
                        $"{id}.cut",
                        "cut"),
                    [BoundaryExecutionMode.Continue] = BoundaryMode(
                        RuleSupport.Conditional,
                        $"{id}.continue",
                        "same architecture"),
                    [BoundaryExecutionMode.Crossfade] = BoundaryMode(
                        RuleSupport.Conditional,
                        $"{id}.crossfade",
                        "same architecture"),
                }));

    private static ArchitectureBoundaryModePolicy BoundaryMode(
        RuleSupport support,
        string code,
        string reason) =>
        new(
            support,
            code,
            reason,
            FrameStep: 1,
            MinFrames: 1,
            MaxFrames: 8,
            DefaultFrames: 1,
            ContinuityExtraFrames: 0,
            TargetRequiresGeneratedEntry: false,
            TargetRequiresStage: false,
            TargetDisallowsInitialReference: false);

    private sealed class MatchingModule(
        VideoArchitectureDescriptor descriptor,
        VideoArchitectureDescriptor resolvedDescriptor = null) : IVideoArchitectureModule
    {
        public VideoArchitectureDescriptor Descriptor => descriptor;

        public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
        {
            resolved = new(
                model.Name,
                descriptor.Id,
                descriptor.Profiles[0].Id,
                resolvedDescriptor ?? descriptor);
            return true;
        }

        public ArchitectureClipCompilation ValidateAndCompileClip(
            ClipSpec clip,
            IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
            ArchitectureClipCompileContext context) =>
            new(new FakePayload(descriptor.Id), []);
    }

    private sealed record FakePayload(
        ArchitectureId ArchitectureId) :
        IArchitectureClipPayload,
        IArchitectureStagePayloadSource
    {
        public IArchitectureStagePayload GetStagePayload(int rawStageIndex) =>
            new FakeStagePayload(ArchitectureId);
    }

    private sealed record FakeStagePayload(
        ArchitectureId ArchitectureId) : IArchitectureStagePayload;

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

    private sealed class RecordingSessionFactory(
        ArchitectureId architectureId,
        ICollection<string> calls,
        ICollection<ArchitectureClipRuntimeContext> contexts = null)
        : IArchitectureGenerationSessionFactory
    {
        public ArchitectureId ArchitectureId => architectureId;

        public IArchitectureBoundaryAssembler BoundaryAssembler => null;

        public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
        {
        }

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context) =>
            new RecordingSession(architectureId, calls, contexts);

        public void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
        {
        }
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

    private sealed class ExclusiveFinalizerFactory(
        ArchitectureId architectureId,
        ICollection<string> calls) : IArchitectureGenerationSessionFactory
    {
        public ArchitectureId ArchitectureId => architectureId;

        public IArchitectureBoundaryAssembler BoundaryAssembler => null;

        public ArchitectureTimelineFinalizerScope FinalizerScope =>
            ArchitectureTimelineFinalizerScope.WholeTimelineExclusive;

        public bool HasFinalizationWork(
            ArchitectureTimelineFinalizationContext context) => true;

        public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
        {
        }

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context) =>
            throw new NotSupportedException();

        public void FinalizeTimeline(ArchitectureTimelineFinalizationContext context) =>
            calls.Add(architectureId.Value);
    }
}
