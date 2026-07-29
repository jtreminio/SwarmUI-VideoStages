using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// The Wan module's own capability and payload contract. Shared recognition and descriptor
/// invariants live in <see cref="RealArchitectureContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public class WanArchitectureTests
{
    [Fact]
    public void Does_not_claim_base_or_cross_family_models()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle wan = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel ltxVideo = TestModelFactory.CreateBaseAndLtxv2VideoModels().VideoModel;

        Assert.False(WanArchitectureModule.Instance.TryResolveModel(wan.BaseModel, out _));
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(ltxVideo, out _));
        Assert.False(Ltx2ArchitectureModule.Instance.TryResolveModel(wan.VideoModel, out _));
    }

    [Fact]
    public void Boundary_policy_is_cut_only_on_the_Wan_frame_grid()
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;

        Assert.Equal(
            RuleSupport.Supported,
            descriptor.BoundaryRules[BoundaryExecutionMode.Cut].Support);
        Assert.Equal(
            RuleSupport.Unsupported,
            descriptor.BoundaryRules[BoundaryExecutionMode.Continue].Support);
        Assert.Equal(
            RuleSupport.Unsupported,
            descriptor.BoundaryRules[BoundaryExecutionMode.Crossfade].Support);
        Assert.Equal(
            WanArchitectureModule.FrameGrid,
            Assert.Single(descriptor.Profiles).FrameGrid);
    }

    [Fact]
    public void Descriptor_advertises_same_clip_stage_chaining_inputs()
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;

        Assert.True(descriptor.Capabilities.Architecture.HasFlag(
            ArchitectureCapability.MultiStage));
        Assert.True(descriptor.Capabilities.Architecture.HasFlag(
            ArchitectureCapability.SourcedEntry));
        Assert.True(descriptor.Capabilities.Clip.HasFlag(ClipCapability.SourceVideo));
        Assert.Contains(ArchitectureEntryMode.SourceVideo, descriptor.EntryModes);
        Assert.DoesNotContain(ArchitectureEntryMode.RefineVideo, descriptor.EntryModes);
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.ImageInput));
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.VideoInput));
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.Lora));
        Assert.True(Assert.Single(descriptor.Profiles).Capabilities.HasFlag(
            ModelProfileCapability.NormalLora));
        Assert.True(descriptor.StageGuideReferences.Allows(
            StageGuideReferencePolicy.Classify("Generated")));
        Assert.True(descriptor.StageGuideReferences.Allows(
            StageGuideReferencePolicy.Classify("PreviousStage")));
    }

    [Fact]
    public void Swap_compatibility_requires_the_same_architecture_and_profile()
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        ResolvedVideoModel swap = new(
            "wan-low",
            descriptor.Id,
            WanArchitectureModule.ImageToVideoProfileId,
            descriptor);
        ResolvedVideoModel sameProfile = swap with { ModelName = "wan-high" };
        ResolvedVideoModel otherProfile = sameProfile with
        {
            ModelProfileId = new("synthetic-other-wan-profile"),
        };
        ResolvedVideoModel otherArchitecture = sameProfile with
        {
            ArchitectureId = new("synthetic-other-architecture"),
        };

        Assert.True(WanExecutionAdapter.IsSwapCompatible(swap, sameProfile));
        Assert.False(WanExecutionAdapter.IsSwapCompatible(swap, otherProfile));
        Assert.False(WanExecutionAdapter.IsSwapCompatible(swap, otherArchitecture));
        Assert.False(WanExecutionAdapter.IsSwapCompatible(swap, null));
        string mismatch =
            WanExecutionAdapter.DescribeSwapIncompatibility(swap, 2, 10, otherProfile);
        Assert.Contains("'wan-low'", mismatch);
        Assert.Contains("clip 2 stage 10", mismatch);
        Assert.Contains("'wan-high'", mismatch);
        Assert.Contains("'synthetic-other-wan-profile'", mismatch);
    }

    [Fact]
    public void Capability_validation_rejects_what_the_first_slice_does_not_support()
    {
        StageSpec stage = Stage(10, "wan-model");

        AssertRejected(GeneratedClip(0, stage with { Upscale = 2 }), "upscale");
        AssertRejected(
            GeneratedClip(0, stage with { RetakeWindow = new(0, 8, 1) }),
            "retake");
        AssertRejected(
            GeneratedClip(0, stage) with { ImageRefs = [new("Generated", 1, false, null)] },
            "image references");
        AssertRejected(
            GeneratedClip(0, stage) with { PromptWindows = [new("late", 1, 1)] },
            "prompt relay");
        AssertRejected(
            GeneratedClip(0, stage) with { ReuseAudio = true },
            "captured stage audio reuse");
        AssertRejected(
            GeneratedClip(0, stage) with { ClipLengthFromAudio = true },
            "audio-derived clip duration");
        AssertRejected(
            GeneratedClip(0, stage) with { ClipLengthFromControlNet = true },
            "control-signal-derived clip duration");
        AssertRejected(
            GeneratedClip(0, stage with { ImageReference = "Base" }),
            "stage image reference 'Base'");
        AssertRejected(
            GeneratedClip(0, stage) with
            {
                IcLoras = [new("wan-ic.safetensors", "Upload", 1, 1, "canny", null)],
            },
            "IC-LoRA");
    }

    [Fact]
    public void Compilation_attaches_typed_payloads_and_previous_stage_input()
    {
        StageSpec first = Stage(10, "wan-model") with { Control = 1 };
        StageSpec second = Stage(11, "wan-model") with
        {
            Control = 0.35,
            Steps = 17,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };
        ClipSpec clip = GeneratedClip(0, first, second);

        ClipPlan compiled = Assert.Single(Compile(clip).Clips);

        WanClipPayload payload = compiled.RequireWanPayload();
        Assert.Equal(WanArchitectureModule.ArchitectureId, payload.ArchitectureId);
        Assert.Equal(2, compiled.Stages.Count);
        Assert.Equal(StageInputKind.RootMedia, compiled.Stages[0].Input);
        Assert.Equal(StageInputKind.PreviousStage, compiled.Stages[1].Input);
        WanStagePayload firstPayload = compiled.Stages[0].RequireWanPayload();
        WanStagePayload secondPayload = compiled.Stages[1].RequireWanPayload();
        Assert.Equal("wan-model", firstPayload.Model);
        Assert.Equal(1, firstPayload.Control);
        Assert.Equal(12, firstPayload.Steps);
        Assert.Equal(4.5, firstPayload.CfgScale);
        Assert.Equal("euler", firstPayload.Sampler);
        Assert.Equal("normal", firstPayload.Scheduler);
        Assert.Equal(0.35, secondPayload.Control);
        Assert.Equal(17, secondPayload.Steps);
        Assert.Empty(firstPayload.Loras);
        Assert.Empty(secondPayload.Loras);
    }

    [Fact]
    public void Catalog_and_profile_advertise_normal_LoRA_support()
    {
        JObject catalog = ArchitectureCatalogSerializer.Serialize(new WanCatalogRegistry());
        JObject wan = Assert.Single(
            catalog["architectures"].Values<JObject>(),
            item => item.Value<string>("id") == "wan22");
        JObject profile = Assert.Single(wan["profiles"].Values<JObject>());
        JObject rule = Assert.Single(profile["rules"].Values<JObject>());

        Assert.Contains("lora", wan["capabilities"]["stage"].Values<string>());
        Assert.Contains(
            "normal-lora",
            profile["capabilities"].Values<string>());
        Assert.Equal(
            WanArchitectureModule.NormalLoraRequiresSamplingStageCode,
            rule.Value<string>("code"));
        Assert.Equal("conditional", rule.Value<string>("support"));
        Assert.Equal("stage", rule.Value<string>("scope"));
        Assert.Equal(
            0,
            rule["constraints"].Value<double>("exclusiveMinimumControl"));
        Assert.Equal(
            0,
            WanArchitectureModule.NormalLoraRequiresSamplingStageRule
                .Require<MinimumStageControlRuleConstraints>()
                .ExclusiveMinimumControl);
    }

    [Fact]
    public void Compilation_plans_clip_then_stage_LoRAs_with_effective_weights()
    {
        StageSpec stage = Stage(10, "wan-model") with
        {
            LoraWeights = [0.35, 0],
            Loras = [new("stage-zero", 0, 0.8)],
        };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            Loras =
            [
                new("clip-active", 1, 0.25),
                new("clip-disabled", 0.9, 0.7),
            ],
        };

        WanStagePayload payload = Assert.Single(
            Assert.Single(Compile(clip).Clips).Stages).RequireWanPayload();

        Assert.Collection(
            payload.Loras,
            lora =>
            {
                Assert.Equal("clip-active", lora.Name);
                Assert.Equal(0.35, lora.ModelWeight);
                Assert.Equal(0.25, lora.TextEncoderWeight);
            },
            lora =>
            {
                Assert.Equal("stage-zero", lora.Name);
                Assert.Equal(0, lora.ModelWeight);
                Assert.Equal(0.8, lora.TextEncoderWeight);
            });
    }

    [Fact]
    public void Compilation_accepts_bounded_sourced_stage_zero_and_previous_stage_chaining()
    {
        StageSpec first = Stage(10, "wan-model") with { Control = 0.5 };
        StageSpec second = Stage(11, "wan-model") with
        {
            Control = 1,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };
        ClipPlan compiled = Assert.Single(
            Compile(SourcedClip(0, first, second)).Clips);

        Assert.Equal(ArchitectureEntryMode.SourceVideo, compiled.EntryMode);
        Assert.Equal(ClipInputKind.SourceVideo, compiled.Input);
        Assert.Equal(StageInputKind.SourceVideo, compiled.Stages[0].Input);
        Assert.Equal(StageInputKind.PreviousStage, compiled.Stages[1].Input);
        Assert.Equal(0.5, compiled.Stages[0].RequireWanPayload().Control);
        Assert.Equal(1, compiled.Stages[1].RequireWanPayload().Control);
    }

    [Fact]
    public void Compilation_canonicalizes_an_authored_model_alias()
    {
        StageSpec stage = Stage(10, "wan-model-alias");
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        ResolvedVideoModel resolved = new(
            "canonical-wan-model.safetensors",
            descriptor.Id,
            WanArchitectureModule.ImageToVideoProfileId,
            descriptor);

        WanClipPlanCompilation compilation = WanClipPlanCompiler.Compile(
            GeneratedClip(0, stage),
            new Dictionary<int, ResolvedVideoModel>
            {
                [stage.ClipStageRawIndex] = resolved,
            });

        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(
            resolved.ModelName,
            Assert.Single(compilation.Stages).Value.Model);
    }

    [Fact]
    public void Compilation_accepts_exact_zero_as_decoded_input_passthrough()
    {
        StageSpec first = Stage(10, "wan-model") with { Control = 0 };
        StageSpec second = Stage(11, "wan-model") with
        {
            Control = 0,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };

        ClipPlan compiled = Assert.Single(
            Compile(SourcedClip(0, first, second)).Clips);

        Assert.All(compiled.Stages, stage => Assert.True(stage.IsPassthrough));
        Assert.Equal(StageInputKind.SourceVideo, compiled.Stages[0].Input);
        Assert.Equal(StageInputKind.PreviousStage, compiled.Stages[1].Input);
        Assert.All(
            compiled.Stages,
            stage => Assert.Equal(0, stage.RequireWanPayload().Control));
    }

    [Fact]
    public void Compilation_refuses_effective_LoRAs_on_samplerless_passthrough()
    {
        StageSpec passthrough = Stage(10, "wan-model") with { Control = 0 };

        AssertBlocked(
            Compile(
                SourcedClip(0, passthrough) with
                {
                    Loras = [new("clip-active", 0.5)],
                }),
            WanArchitectureModule.NormalLoraRequiresSamplingStageCode,
            WanArchitectureModule.NormalLoraRequiresSamplingStageReason);
        AssertBlocked(
            Compile(
                SourcedClip(
                    0,
                    passthrough with
                    {
                        Loras = [new("stage-text-only", 0, 0.8)],
                    })),
            WanArchitectureModule.NormalLoraRequiresSamplingStageCode,
            WanArchitectureModule.NormalLoraRequiresSamplingStageReason);
        AssertBlocked(
            Compile(
                SourcedClip(
                    0,
                    passthrough with
                    {
                        Control = -0.1,
                        Loras = [new("stage-negative-control", 1)],
                    })),
            WanArchitectureModule.NormalLoraRequiresSamplingStageCode,
            WanArchitectureModule.NormalLoraRequiresSamplingStageReason);

        ClipSpec disabled = SourcedClip(
            0,
            passthrough with { LoraWeights = [0] }) with
        {
            Loras = [new("clip-disabled", 1)],
        };
        ClipPlan compiled = Assert.Single(Compile(disabled).Clips);
        Assert.True(Assert.Single(compiled.Stages).IsPassthrough);
        Assert.Empty(compiled.Stages[0].RequireWanPayload().Loras);

        VideoExecutionPlan stageNoOp = Compile(
            SourcedClip(
                0,
                passthrough with
                {
                    Loras = [new("stage-no-op", 0, 0)],
                }));
        Assert.DoesNotContain(
            stageNoOp.Diagnostics,
            diagnostic =>
                diagnostic.Code
                    == WanArchitectureModule.NormalLoraRequiresSamplingStageCode);
        Assert.Empty(
            Assert.Single(stageNoOp.Clips).Stages[0].RequireWanPayload().Loras);

        VideoExecutionPlan sampled = Compile(
            SourcedClip(0, passthrough with { Control = 0.5 }) with
            {
                Loras = [new("clip-active", 0.5)],
            });
        Assert.DoesNotContain(
            sampled.Diagnostics,
            diagnostic =>
                diagnostic.Code
                    == WanArchitectureModule.NormalLoraRequiresSamplingStageCode);
        Assert.Single(
            Assert.Single(sampled.Clips).Stages[0].RequireWanPayload().Loras);
    }

    [Fact]
    public void Static_generated_frames_use_the_resolved_profile_grid_not_the_Wan_constant()
    {
        Assert.Equal(4, WanArchitectureModule.FrameGrid);
        ModelProfileId profileId = new("synthetic-grid-8");
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor with
            {
                Profiles =
                [
                    Assert.Single(WanArchitectureModule.Instance.Descriptor.Profiles) with
                    {
                        Id = profileId,
                        FrameGrid = 8,
                    },
                ],
            };
        ResolvedVideoModel resolved = new(
            "synthetic-wan",
            descriptor.Id,
            profileId,
            descriptor);

        WanStaticGeneratedFrameResolution resolution =
            WanStaticGeneratedFrameResolver.Resolve(16, 2, 10, resolved);

        Assert.Equal(8, resolution.FrameGrid);
        Assert.Equal(9, resolution.Frames);
    }

    [Fact]
    public void Static_generated_frame_resolution_fails_closed_without_a_resolved_model()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WanStaticGeneratedFrameResolver.Resolve(16, 2, 10, null));

        Assert.Equal(
            "Clip 2 stage 10 has no resolved video model.",
            error.Message);
    }

    [Fact]
    public void Static_generated_frame_resolution_fails_closed_for_an_undeclared_profile()
    {
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor;
        ModelProfileId undeclared = new("undeclared-grid");
        ResolvedVideoModel resolved = new(
            "synthetic-wan",
            descriptor.Id,
            undeclared,
            descriptor);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WanStaticGeneratedFrameResolver.Resolve(16, 2, 10, resolved));

        Assert.Equal(
            "Clip 2 stage 10 resolved undeclared model profile 'undeclared-grid'.",
            error.Message);
    }

    /// <summary>
    /// Settings the common capability validator does not inspect. Each one would otherwise compile
    /// into a payload that silently omits it.
    /// </summary>
    [Fact]
    public void Direct_compiler_refuses_invalid_stage_inputs_controls_and_quantized_schedules()
    {
        StageSpec stage = Stage(10, "wan-model");

        AssertRefused(
            GeneratedClip(0, stage with { Control = 0 }),
            "generated-root stage that generates nothing");
        AssertRefused(
            GeneratedClip(0, stage with { Control = 0.8 }),
            "first-stage control");
        AssertRefused(
            GeneratedClip(0, stage with { Control = double.NaN }),
            "first-stage control");
        AssertRefused(
            GeneratedClip(
                0,
                stage,
                stage with
                {
                    Id = 11,
                    Control = -0.1,
                    ImageReference = "PreviousStage",
                    ClipStageIndex = 1,
                    ClipStageRawIndex = 1,
                }),
            "finite range [0, 1]");
        AssertRefused(
            GeneratedClip(
                0,
                stage,
                stage with
                {
                    Id = 11,
                    Control = 1.1,
                    ImageReference = "PreviousStage",
                    ClipStageIndex = 1,
                    ClipStageRawIndex = 1,
                }),
            "finite range [0, 1]");
        AssertRefused(
            GeneratedClip(
                0,
                stage,
                stage with
                {
                    Id = 11,
                    Control = 0.5,
                    ClipStageIndex = 1,
                    ClipStageRawIndex = 1,
                }),
            "later-stage input");
        AssertRefused(
            GeneratedClip(
                0,
                stage,
                stage with
                {
                    Id = 11,
                    Control = 0.9,
                    Steps = 8,
                    ImageReference = "PreviousStage",
                    ClipStageIndex = 1,
                    ClipStageRawIndex = 1,
                }),
            "quantizes to sampler start step 0");
        AssertRefused(
            SourcedClip(0, stage with { Control = 1.1 }),
            "decoded-input control outside the finite range [0, 1]");
        AssertRefused(
            SourcedClip(0, stage with { Control = double.NaN }),
            "decoded-input control outside the finite range [0, 1]");
        AssertRefused(
            SourcedClip(
                0,
                stage with
                {
                    Control = 0.5,
                    ImageReference = "PreviousStage",
                }),
            "first-stage input other than 'Generated'");
        AssertRefused(
            SourcedClip(0, stage with { Control = 0.9, Steps = 8 }),
            "quantizes to sampler start step 0");
    }

    [Fact]
    public void Compiler_refuses_a_declared_but_noncanonical_Wan_stage_profile()
    {
        StageSpec first = Stage(10, "wan-current");
        StageSpec second = Stage(11, "wan-alternate") with
        {
            Control = 0.5,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };
        ClipSpec clip = GeneratedClip(0, first, second);
        VideoArchitectureDescriptor canonical =
            WanArchitectureModule.Instance.Descriptor;
        ModelProfileId alternateId = new("synthetic-wan-alternate");
        VideoArchitectureDescriptor declaredAlternate = canonical with
        {
            Profiles =
            [
                .. canonical.Profiles,
                Assert.Single(canonical.Profiles) with
                {
                    Id = alternateId,
                    DisplayName = "Synthetic Wan alternate",
                },
            ],
        };
        Dictionary<int, ResolvedVideoModel> stageModels = new()
        {
            [0] = new(
                first.Model,
                canonical.Id,
                WanArchitectureModule.ImageToVideoProfileId,
                canonical),
            [1] = new(
                second.Model,
                declaredAlternate.Id,
                alternateId,
                declaredAlternate),
        };

        ArchitectureClipCompilation compilation =
            WanArchitectureModule.Instance.ValidateAndCompileClip(
                clip,
                stageModels,
                new(512, 512, 24));

        PlanDiagnostic diagnostic = Assert.Single(
            compilation.Diagnostics,
            item => item.Code == "wan22.stage-profile.unsupported");
        Assert.Equal(11, diagnostic.StageId);
        Assert.Contains("synthetic-wan-alternate", diagnostic.Message);
        Assert.Contains(WanArchitectureModule.ImageToVideoProfileId.ToString(), diagnostic.Message);
    }

    [Fact]
    public void Schedule_policy_preserves_one_step_partial_boundary()
    {
        Assert.True(WanStageSchedulePolicy.IsQuantizedZeroPartial(8, 0.9));
        Assert.False(WanStageSchedulePolicy.IsQuantizedZeroPartial(8, 0.87));
        Assert.Equal(1, WanStageSchedulePolicy.StartStep(8, 0.87));
    }

    private static void AssertRejected(ClipSpec clip, string expectedOption)
    {
        VideoExecutionPlan plan = Compile(clip);
        AssertBlocked(
            plan,
            "architecture-capability-unsupported",
            expectedOption);
        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Code == "wan22.option.unsupported"
                && item.Message.Contains(expectedOption));
    }

    private static void AssertRefused(ClipSpec clip, string expectedOption) =>
        AssertBlocked(Compile(clip), "wan22.option.unsupported", expectedOption);

    private static void AssertBlocked(
        VideoExecutionPlan plan,
        string code,
        string expectedOption)
    {
        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == code
                && item.Severity == PlanDiagnosticSeverity.Error
                && item.Message.Contains(expectedOption));
        Assert.Null(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    private static VideoExecutionPlan Compile(ClipSpec clip)
    {
        VideoStagesSpec spec = new(512, 512, 24, false, [clip]);
        return VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));
    }

    /// <summary>Assigns Wan to every clip without needing host model metadata installed.</summary>
    private static ArchitecturePlanningResult ResolveWan(VideoStagesSpec spec)
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        Dictionary<int, ClipArchitectureAssignment> clips = [];
        foreach (ClipSpec clip in spec.Clips)
        {
            Dictionary<int, ResolvedVideoModel> stageModels = [];
            foreach (StageSpec stage in clip.Stages ?? [])
            {
                stageModels[stage.ClipStageRawIndex] = new(
                    stage.Model,
                    descriptor.Id,
                    WanArchitectureModule.ImageToVideoProfileId,
                    descriptor);
            }
            clips.TryAdd(clip.Id, new(
                clip.Id,
                WanArchitectureModule.Instance,
                descriptor,
                stageModels));
        }
        return new(clips, []);
    }

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

    private static ClipSpec SourcedClip(int id, params StageSpec[] stages) =>
        GeneratedClip(id, stages) with
        {
            SourceVideo = new("data", "source.mp4", 0),
        };

    private static StageSpec Stage(int id, string model) =>
        new(id, 1, 1, "pixel-lanczos", model, 12, 4.5, "euler", "normal", "Generated");

    private sealed class WanCatalogRegistry : IVideoArchitectureRegistry
    {
        public IReadOnlyList<VideoArchitectureDescriptor> Catalog =>
            [WanArchitectureModule.Instance.Descriptor];

        public IReadOnlyList<ResolvedVideoModel> ResolvedModels => [];

        public IVideoArchitectureModule GetModule(ArchitectureId architectureId) =>
            architectureId == WanArchitectureModule.ArchitectureId
                ? WanArchitectureModule.Instance
                : throw new KeyNotFoundException();

        public bool TryResolveModel(string modelName, out ResolvedVideoModel resolved)
        {
            resolved = null;
            return false;
        }

        public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved) =>
            WanArchitectureModule.Instance.TryResolveModel(model, out resolved);
    }
}
