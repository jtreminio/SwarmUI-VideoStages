using Newtonsoft.Json.Linq;
using SwarmUI.Core;
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
    public void Resolves_host_supported_Wan_image_models_and_rejects_non_model_shapes()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle fourteen =
            TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        TestModelBundle five =
            TestModelFactory.CreateBaseAndWan22Ti2v5bModels();

        Assert.True(WanArchitectureModule.Instance.TryResolveModel(
            fourteen.VideoModel,
            out ResolvedVideoModel resolvedFourteen));
        Assert.Equal(
            WanArchitectureModule.ImageToVideoProfileId,
            resolvedFourteen.ModelProfileId);
        Assert.True(WanArchitectureModule.Instance.TryResolveModel(
            five.VideoModel,
            out ResolvedVideoModel resolvedFive));
        Assert.Equal(WanArchitectureModule.Ti2v5bProfileId, resolvedFive.ModelProfileId);

        T2IModelClass exactFive = five.VideoModel.ModelClass;
        five.VideoModel.ModelClass = exactFive with
        {
            CompatClass = T2IModelClassSorter.CompatWan21_14b,
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(five.VideoModel, out _));
        five.VideoModel.ModelClass = exactFive with
        {
            ID = $"{WanArchitectureModule.Ti2v5bModelClassId}/lora",
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(five.VideoModel, out _));
        five.VideoModel.ModelClass = exactFive with
        {
            ID = "wan-2_1-image2video-14b",
            CompatClass = T2IModelClassSorter.CompatWan21_14b,
        };
        Assert.True(WanArchitectureModule.Instance.TryResolveModel(
            five.VideoModel,
            out ResolvedVideoModel ordinary));
        Assert.Equal(
            WanArchitectureModule.OrdinaryImageToVideoProfileId,
            ordinary.ModelProfileId);
        five.VideoModel.ModelClass = exactFive with
        {
            ID = "wan-2_1-vace-14b",
            CompatClass = T2IModelClassSorter.CompatWan21_14b,
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(five.VideoModel, out _));

        foreach ((string modelClassId, T2IModelCompatClass compatibility) in new[]
        {
            ("wan-2_1-image2video-1_3b", T2IModelClassSorter.CompatWan21_1_3b),
            ("wan-2_1-flf2v-14b", T2IModelClassSorter.CompatWan21_14b),
        })
        {
            five.VideoModel.ModelClass = exactFive with
            {
                ID = modelClassId,
                CompatClass = compatibility,
            };
            Assert.True(WanArchitectureModule.Instance.TryResolveModel(
                five.VideoModel,
                out ResolvedVideoModel accepted));
            Assert.Equal(
                WanArchitectureModule.OrdinaryImageToVideoProfileId,
                accepted.ModelProfileId);
            Assert.Equal(compatibility.ID, accepted.CompatibilityClassId);
            Assert.Equal(
                VideoModelEntryAbility.TextToVideo
                    | VideoModelEntryAbility.ImageToVideo,
                accepted.EntryAbilities);
        }
        foreach ((string modelClassId, T2IModelCompatClass compatibility) in new[]
        {
            ("wan-2_1-text2video-1_3b", T2IModelClassSorter.CompatWan21_1_3b),
            ("wan-2_1-text2video-14b", T2IModelClassSorter.CompatWan21_14b),
            ("wan-2_2-text2video-14b", T2IModelClassSorter.CompatWan21_14b),
        })
        {
            five.VideoModel.ModelClass = exactFive with
            {
                ID = modelClassId,
                CompatClass = compatibility,
            };
            Assert.True(WanArchitectureModule.Instance.TryResolveModel(
                five.VideoModel,
                out ResolvedVideoModel accepted));
            Assert.Equal(
                WanArchitectureModule.OrdinaryImageToVideoProfileId,
                accepted.ModelProfileId);
            Assert.Equal(
                VideoModelEntryAbility.TextToVideo
                    | VideoModelEntryAbility.ImageToVideo,
                accepted.EntryAbilities);
        }
        foreach (string rejectedClassId in new[]
        {
            "wan-2_1-text2video/vae",
            "wan-2_1-text2video-14b/lora",
            "wan-2_1-text2video-vace-14b",
            "wan-2_1-image2video-14b/lora",
            "wan-2_1-image2video-lora",
            "wan-2_1-image2video_vae",
            "wan-2_1-image2video(lora)",
            "wan-2_1-image2video+vae",
            "wan-2_1-image2video:lora",
            "wan-2_1-image2video@vae",
            "wan-2_1-vace-1_3b",
            "wan-2_1-vace-14b",
            "not-wan-image2video",
        })
        {
            five.VideoModel.ModelClass = exactFive with
            {
                ID = rejectedClassId,
                CompatClass = T2IModelClassSorter.CompatWan21_14b,
            };
            Assert.False(WanArchitectureModule.Instance.TryResolveModel(
                five.VideoModel,
                out _));
        }
        five.VideoModel.ModelClass = exactFive with
        {
            ID = "wan-2_1-image2video-valor",
            CompatClass = T2IModelClassSorter.CompatWan21_14b,
        };
        Assert.True(WanArchitectureModule.Instance.TryResolveModel(
            five.VideoModel,
            out _));
        five.VideoModel.ModelClass = exactFive with
        {
            ID = "wan-2_1-text2video-14b",
            CompatClass = T2IModelClassSorter.CompatLtxv2,
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(five.VideoModel, out _));
        five.VideoModel.ModelClass = exactFive with
        {
            ID = "wan-2_1-image2video-14b",
            CompatClass = T2IModelClassSorter.CompatLtxv2,
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(five.VideoModel, out _));
        five.VideoModel.ModelClass = exactFive with
        {
            ID = "wan-2_1-image2video-14b",
            CompatClass = T2IModelClassSorter.CompatWan21_14b with
            {
                IsImage2Video = false,
                IsText2Video = false,
            },
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(five.VideoModel, out _));

        JObject catalog = ArchitectureCatalogSerializer.Serialize(
            new WanCatalogRegistry(resolvedFive));
        JObject catalogModel = Assert.Single(catalog["models"].Values<JObject>());
        Assert.Equal(five.VideoModel.Name, catalogModel.Value<string>("modelName"));
        Assert.Equal(
            WanArchitectureModule.ArchitectureId.Value,
            catalogModel.Value<string>("architectureId"));
        Assert.Equal(
            WanArchitectureModule.Ti2v5bProfileId.Value,
            catalogModel.Value<string>("modelProfileId"));
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
        Assert.All(
            descriptor.Profiles,
            profile => Assert.Equal(WanArchitectureModule.FrameGrid, profile.FrameGrid));
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
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.PixelUpscale));
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.Lora));
        Assert.All(
            descriptor.Profiles,
            profile => Assert.True(profile.Capabilities.HasFlag(
                ModelProfileCapability.NormalLora)));
        Assert.True(descriptor.StageGuideReferences.Allows(
            StageGuideReferencePolicy.Classify("Generated")));
        Assert.True(descriptor.StageGuideReferences.Allows(
            StageGuideReferencePolicy.Classify("PreviousStage")));
    }

    [Fact]
    public void Effective_request_accepts_pixel_upscale_and_ignores_advanced_upscalers_and_ic_lora()
    {
        StageSpec stage = Stage(10, "wan-model");

        VideoExecutionPlan pixelPlan = Compile(
            GeneratedClip(0, stage with { Upscale = 2 }));
        Assert.DoesNotContain(
            pixelPlan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        AssertIgnored(
            GeneratedClip(
                0,
                stage with
                {
                    Upscale = 2,
                    UpscaleMethod = "model-fake-upscaler.safetensors",
                }),
            "effective-request.wan-advanced-upscale-ignored");
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
        AssertIgnored(
            GeneratedClip(0, stage) with
            {
                IcLoras = [new("wan-ic.safetensors", "Upload", 1, 1, "canny", null)],
            },
            "effective-request.wan-ic-lora-ignored");
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
        Assert.Equal(
            WanArchitectureModule.ImageToVideoProfileId,
            compiled.RequireWanPayload().ProfileId);
        Assert.Equal(WanArchitectureModule.ImageToVideoProfileId, firstPayload.ProfileId);
        Assert.Equal(WanArchitectureModule.ImageToVideoProfileId, secondPayload.ProfileId);
        Assert.Equal("wan-model", firstPayload.Model);
        Assert.Equal(1, firstPayload.Control);
        Assert.Equal(12, firstPayload.Steps);
        Assert.Equal(4.5, firstPayload.CfgScale);
        Assert.Equal("euler", firstPayload.Sampler);
        Assert.Equal("normal", firstPayload.Scheduler);
        Assert.Equal(StageUpscaleMode.None, firstPayload.Upscale.Mode);
        Assert.Equal(1, firstPayload.Upscale.Factor);
        Assert.Equal(0.35, secondPayload.Control);
        Assert.Equal(17, secondPayload.Steps);
        Assert.Empty(firstPayload.Loras);
        Assert.Empty(secondPayload.Loras);
    }

    [Fact]
    public void Compilation_carries_a_normalized_Wan_pixel_upscale()
    {
        StageSpec stage = Stage(10, "wan-model") with
        {
            Upscale = 1.5,
            UpscaleMethod = "pixel-bicubic",
        };

        WanStagePayload payload = Assert.Single(
            Assert.Single(Compile(GeneratedClip(0, stage)).Clips).Stages)
            .RequireWanPayload();

        Assert.Equal(StageUpscaleMode.Pixel, payload.Upscale.Mode);
        Assert.Equal(1.5, payload.Upscale.Factor);
        Assert.Equal("pixel-bicubic", payload.Upscale.RawMethod);
        Assert.Equal("bicubic", payload.Upscale.MethodName);
    }

    [Fact]
    public void Geometry_projection_warns_when_only_one_Wan_clip_pixel_upscales()
    {
        StageSpec first = Stage(10, "wan-model");
        StageSpec upscaled = Stage(11, "wan-model") with
        {
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
            ImageReference = "PreviousStage",
            Control = 0,
            Upscale = 2,
        };
        VideoStagesSpec spec = new(
            512,
            512,
            24,
            false,
            [
                GeneratedClip(0, first, upscaled),
                GeneratedClip(1, Stage(12, "wan-model")),
            ]);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        PlanDiagnostic diagnostic = Assert.Single(
            plan.Diagnostics,
            item => item.Code == "clip-geometry-will-conform");
        Assert.Equal(0, diagnostic.ClipId);
        Assert.Contains("1024x1024", diagnostic.Message);
        Assert.Contains("512x512", diagnostic.Message);
        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Code == "clip-aspect-mismatch");
    }

    [Fact]
    public void Catalog_and_profile_advertise_normal_LoRA_support()
    {
        JObject catalog = ArchitectureCatalogSerializer.Serialize(new WanCatalogRegistry());
        JObject wan = Assert.Single(
            catalog["architectures"].Values<JObject>(),
            item => item.Value<string>("id") == "wan22");
        JObject[] profiles = [.. wan["profiles"].Values<JObject>()];

        Assert.Contains("lora", wan["capabilities"]["stage"].Values<string>());
        Assert.Equal(3, profiles.Length);
        Assert.Equal(
            [
                WanArchitectureModule.ImageToVideoProfileId.Value,
                WanArchitectureModule.Ti2v5bProfileId.Value,
                WanArchitectureModule.OrdinaryImageToVideoProfileId.Value,
            ],
            profiles.Select(profile => profile.Value<string>("id")));
        Assert.All(profiles, profile =>
        {
            JObject rule = Assert.Single(profile["rules"].Values<JObject>());
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
        });
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
            Loras =
            [
                new("stage-text-only", 0, 0.8),
                new("stage-active", -0.4, 0.9),
            ],
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
                Assert.Equal("stage-active", lora.Name);
                Assert.Equal(-0.4, lora.ModelWeight);
                Assert.Equal(0.9, lora.TextEncoderWeight);
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
    public void Compilation_locks_each_clip_to_one_compatibility_class_but_allows_cut_clips_to_differ()
    {
        StageSpec firstFive = Stage(10, "wan-five");
        StageSpec secondFive = Stage(11, "wan-five") with
        {
            Control = 0.5,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };
        ClipSpec fiveClip = GeneratedClip(0, firstFive, secondFive);
        VideoStagesSpec fiveSpec = new(512, 512, 24, false, [fiveClip]);
        VideoExecutionPlan fivePlan = VideoExecutionPlanCompiler.Compile(
            fiveSpec,
            RootEnvironment.FromSpec(fiveSpec),
            ResolveWan(
                fiveSpec,
                new Dictionary<string, ModelProfileId>
                {
                    ["wan-five"] = WanArchitectureModule.Ti2v5bProfileId,
                }));
        ClipPlan compiledFive = Assert.Single(fivePlan.Clips);
        Assert.DoesNotContain(
            fivePlan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(
            WanArchitectureModule.Ti2v5bProfileId,
            compiledFive.RequireWanPayload().ProfileId);
        Assert.All(
            compiledFive.Stages,
            stage => Assert.Equal(
                WanArchitectureModule.Ti2v5bProfileId,
                stage.RequireWanPayload().ProfileId));

        StageSpec mixedSecond = secondFive with { Model = "wan-fourteen" };
        ClipSpec mixedClip = GeneratedClip(0, firstFive, mixedSecond);
        VideoStagesSpec mixedSpec = new(512, 512, 24, false, [mixedClip]);
        VideoExecutionPlan mixedPlan = VideoExecutionPlanCompiler.Compile(
            mixedSpec,
            RootEnvironment.FromSpec(mixedSpec),
            ResolveWan(
                mixedSpec,
                new Dictionary<string, ModelProfileId>
                {
                    ["wan-five"] = WanArchitectureModule.Ti2v5bProfileId,
                    ["wan-fourteen"] = WanArchitectureModule.ImageToVideoProfileId,
                }));
        Assert.Contains(
            mixedPlan.Diagnostics,
            diagnostic => diagnostic.Code == "wan.clip-compatibility-class.mixed"
                && diagnostic.Severity == PlanDiagnosticSeverity.Error
                && diagnostic.RawStageIndex == 1);
        Assert.Null(Assert.Single(mixedPlan.Clips).ArchitecturePayload);

        VideoStagesSpec cutSpec = new(
            512,
            512,
            24,
            false,
            [
                GeneratedClip(0, Stage(10, "wan-fourteen")),
                GeneratedClip(1, Stage(11, "wan-five")),
            ]);
        VideoExecutionPlan cutPlan = VideoExecutionPlanCompiler.Compile(
            cutSpec,
            RootEnvironment.FromSpec(cutSpec),
            ResolveWan(
                cutSpec,
                new Dictionary<string, ModelProfileId>
                {
                    ["wan-five"] = WanArchitectureModule.Ti2v5bProfileId,
                    ["wan-fourteen"] = WanArchitectureModule.ImageToVideoProfileId,
                }));
        Assert.DoesNotContain(
            cutPlan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(
            [
                WanArchitectureModule.ImageToVideoProfileId,
                WanArchitectureModule.Ti2v5bProfileId,
            ],
            cutPlan.Clips.Select(clip => clip.RequireWanPayload().ProfileId));
    }

    [Fact]
    public void Same_compatibility_class_can_chain_exact_and_ordinary_Wan_aliases()
    {
        StageSpec exact = Stage(10, "wan-2.2-exact");
        StageSpec ordinary = Stage(11, "wan-2.1-ordinary") with
        {
            Control = 0.5,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };

        WanClipPlanCompilation compiled = CompileDirect(
            GeneratedClip(0, exact, ordinary),
            profilesByModel: new Dictionary<string, ModelProfileId>
            {
                [exact.Model] = WanArchitectureModule.ImageToVideoProfileId,
                [ordinary.Model] = WanArchitectureModule.OrdinaryImageToVideoProfileId,
            });

        Assert.DoesNotContain(
            compiled.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(
            [
                WanArchitectureModule.ImageToVideoProfileId,
                WanArchitectureModule.OrdinaryImageToVideoProfileId,
            ],
            compiled.Stages.OrderBy(pair => pair.Key).Select(pair => pair.Value.ProfileId));
    }

    [Fact]
    public void Production_planning_accepts_a_real_Wan_2_1_image_model()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle installed = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel model = AddWanModel(
            installed.VideoModel,
            "UnitTest_Wan21_I2V.safetensors",
            "wan-2_1-image2video-14b",
            T2IModelClassSorter.CompatWan21_14b);
        ClipSpec clip = GeneratedClip(0, Stage(10, model.Name)) with
        {
            AuthoredStages = [new(0, model.Name, null, false)],
        };
        VideoStagesSpec spec = new(512, 512, 24, false, [clip]);
        VideoArchitectureRegistry registry = new(
        [
            NoneArchitectureModule.Instance,
            Ltx2ArchitectureModule.Instance,
            WanArchitectureModule.Instance,
        ]);
        ArchitecturePlanningResult resolution =
            ArchitecturePlanResolver.Resolve(spec, registry);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            resolution);

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(
            WanArchitectureModule.OrdinaryImageToVideoProfileId,
            compiled.Stages[0].ResolvedModel.ModelProfileId);
        Assert.Equal(
            T2IModelClassSorter.CompatWan21_14b.ID,
            compiled.RequireWanPayload().CompatibilityClassId);
    }

    [Fact]
    public void Resolution_rejects_a_skipped_Wan_stage_from_another_compatibility_class()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle installed = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel other = AddWanModel(
            installed.VideoModel,
            "UnitTest_Wan21_I2V_1_3B.safetensors",
            "wan-2_1-image2video-1_3b",
            T2IModelClassSorter.CompatWan21_1_3b);
        ClipSpec clip = GeneratedClip(0, Stage(10, installed.VideoModel.Name)) with
        {
            AuthoredStages =
            [
                new(0, installed.VideoModel.Name, null, false),
                new(1, other.Name, null, true),
            ],
        };
        VideoStagesSpec spec = new(512, 512, 24, false, [clip]);
        VideoArchitectureRegistry registry = new(
        [
            NoneArchitectureModule.Instance,
            Ltx2ArchitectureModule.Instance,
            WanArchitectureModule.Instance,
        ]);

        ArchitecturePlanningResult result =
            ArchitecturePlanResolver.Resolve(spec, registry);

        PlanDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            candidate => candidate.Code
                == "architecture-mixed-authored-stage-compatibility");
        Assert.Equal(1, diagnostic.RawStageIndex);
        Assert.Contains("(skipped)", diagnostic.Message);
    }

    [Fact]
    public void Exact_5b_native_text_root_compiles_to_empty_latent_entry()
    {
        StageSpec stage = Stage(10, "wan-five");
        ClipSpec clip = GeneratedClip(0, stage);
        VideoStagesSpec spec = new(
            Width: 512,
            Height: 512,
            FPS: 24,
            IsTextToVideo: true,
            Clips: [clip]);
        ArchitecturePlanningResult planning = ResolveWan(
            spec,
            new Dictionary<string, ModelProfileId>
            {
                ["wan-five"] = WanArchitectureModule.Ti2v5bProfileId,
            });

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            planning);

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(ArchitectureEntryMode.TextToVideo, compiled.EntryMode);
        Assert.Equal(ClipInputKind.EmptyLatent, compiled.Input);
        Assert.Equal(StageInputKind.EmptyLatent, Assert.Single(compiled.Stages).Input);
        Assert.Equal(
            WanArchitectureModule.Ti2v5bProfileId,
            compiled.RequireWanPayload().ProfileId);
    }

    [Fact]
    public void Exact_5b_native_text_multistage_uses_empty_latent_then_previous_stage()
    {
        StageSpec first = Stage(10, "wan-five") with
        {
            ClipStageRawIndex = 0,
        };
        StageSpec second = Stage(11, "wan-five") with
        {
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
            ImageReference = "PreviousStage",
        };
        ClipSpec clip = GeneratedClip(0, first, second);
        VideoStagesSpec spec = new(512, 512, 24, true, [clip]);
        ArchitecturePlanningResult planning = ResolveWan(
            spec,
            new Dictionary<string, ModelProfileId>
            {
                ["wan-five"] = WanArchitectureModule.Ti2v5bProfileId,
            });

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            planning);

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(
            [StageInputKind.EmptyLatent, StageInputKind.PreviousStage],
            compiled.Stages.Select(stage => stage.Input));
    }

    [Fact]
    public void Exact_14b_native_text_root_uses_family_wide_entry_ability()
    {
        StageSpec stage = Stage(10, "wan-fourteen");
        ClipSpec clip = GeneratedClip(0, stage);
        VideoStagesSpec spec = new(512, 512, 24, true, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(ArchitectureEntryMode.TextToVideo, compiled.EntryMode);
        Assert.Equal(ClipInputKind.EmptyLatent, compiled.Input);
        Assert.Equal(StageInputKind.EmptyLatent, Assert.Single(compiled.Stages).Input);
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
            descriptor)
        {
            ModelClassId = WanArchitectureModule.ImageToVideoModelClassId,
            CompatibilityClassId = T2IModelClassSorter.CompatWan21_14b.ID,
            EntryAbilities = VideoModelEntryAbility.ImageToVideo,
        };

        WanClipPlanCompilation compilation = WanClipPlanCompiler.Compile(
            GeneratedClip(0, stage),
            new Dictionary<int, ResolvedVideoModel>
            {
                [stage.ClipStageRawIndex] = resolved,
            },
            new(512, 512, 24));

        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(
            resolved.ModelName,
            Assert.Single(compilation.Stages).Value.Model);
    }

    [Fact]
    public void Compilation_refuses_a_present_null_stage_resolution_and_uses_canonical_fallback()
    {
        StageSpec stage = Stage(10, "authored-wan-model");
        WanClipPlanCompilation compilation = WanClipPlanCompiler.Compile(
            GeneratedClip(0, stage),
            new Dictionary<int, ResolvedVideoModel>
            {
                [stage.ClipStageRawIndex] = null,
            },
            new(512, 512, 24));

        PlanDiagnostic diagnostic = Assert.Single(
            compilation.Diagnostics,
            item => item.Code == "wan22.stage-profile.unsupported");
        Assert.Equal(PlanDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(stage.Id, diagnostic.StageId);
        Assert.Contains("<missing>", diagnostic.Message);
        Assert.Equal(
            WanArchitectureModule.ImageToVideoProfileId,
            compilation.Payload.ProfileId);
        WanStagePayload fallback = Assert.Single(compilation.Stages).Value;
        Assert.Equal(stage.Model, fallback.Model);
        Assert.Equal(
            WanArchitectureModule.ImageToVideoProfileId,
            fallback.ProfileId);
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
        VideoExecutionPlan textOnly = Compile(
            SourcedClip(
                0,
                passthrough with
                {
                    Loras = [new("stage-text-only", 0, 0.8)],
                }));
        Assert.DoesNotContain(
            textOnly.Diagnostics,
            diagnostic =>
                diagnostic.Code
                    == WanArchitectureModule.NormalLoraRequiresSamplingStageCode);
        Assert.Empty(
            Assert.Single(textOnly.Clips).Stages[0].RequireWanPayload().Loras);
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
                    WanArchitectureModule.Instance.Descriptor.Profiles.Single(
                        profile => profile.Id
                            == WanArchitectureModule.ImageToVideoProfileId) with
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
                canonical.Profiles.Single(
                    profile => profile.Id
                        == WanArchitectureModule.ImageToVideoProfileId) with
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

    private static void AssertIgnored(ClipSpec clip, string code)
    {
        VideoExecutionPlan plan = Compile(clip);
        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == code
                && item.Severity == PlanDiagnosticSeverity.Warning);
        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Severity == PlanDiagnosticSeverity.Error);
        Assert.NotNull(Assert.Single(plan.Clips).ArchitecturePayload);
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

    private static WanClipPlanCompilation CompileDirect(
        ClipSpec clip,
        ArchitectureEntryMode entryMode = ArchitectureEntryMode.ImageToVideo,
        IReadOnlyDictionary<string, ModelProfileId> profilesByModel = null)
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        Dictionary<int, ResolvedVideoModel> stageModels = [];
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            stageModels.Add(
                stage.ClipStageRawIndex,
                ResolvedWan(
                    stage.Model,
                    profilesByModel is not null
                        && profilesByModel.TryGetValue(
                            stage.Model,
                            out ModelProfileId profileId)
                            ? profileId
                            : WanArchitectureModule.ImageToVideoProfileId,
                    descriptor));
        }
        return WanClipPlanCompiler.Compile(
            clip,
            stageModels,
            new(512, 512, 24, entryMode));
    }

    /// <summary>Assigns Wan to every clip without needing host model metadata installed.</summary>
    private static ArchitecturePlanningResult ResolveWan(
        VideoStagesSpec spec,
        IReadOnlyDictionary<string, ModelProfileId> profilesByModel = null)
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        Dictionary<int, ClipArchitectureAssignment> clips = [];
        foreach (ClipSpec clip in spec.Clips)
        {
            Dictionary<int, ResolvedVideoModel> stageModels = [];
            foreach (StageSpec stage in clip.Stages ?? [])
            {
                stageModels[stage.ClipStageRawIndex] = ResolvedWan(
                    stage.Model,
                    profilesByModel is not null
                        && profilesByModel.TryGetValue(stage.Model, out ModelProfileId profileId)
                            ? profileId
                            : WanArchitectureModule.ImageToVideoProfileId,
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

    private static ResolvedVideoModel ResolvedWan(
        string model,
        ModelProfileId profile,
        VideoArchitectureDescriptor descriptor)
    {
        bool five = profile == WanArchitectureModule.Ti2v5bProfileId;
        bool ordinary = profile == WanArchitectureModule.OrdinaryImageToVideoProfileId;
        return new(model, descriptor.Id, profile, descriptor)
        {
            ModelClassId = five
                ? WanArchitectureModule.Ti2v5bModelClassId
                : ordinary
                    ? "wan-2_1-image2video-14b"
                    : WanArchitectureModule.ImageToVideoModelClassId,
            CompatibilityClassId = five
                ? T2IModelClassSorter.CompatWan22_5b.ID
                : T2IModelClassSorter.CompatWan21_14b.ID,
            EntryAbilities = VideoModelEntryAbility.TextToVideo
                | VideoModelEntryAbility.ImageToVideo,
        };
    }

    private static T2IModel AddWanModel(
        T2IModel template,
        string name,
        string modelClassId,
        T2IModelCompatClass compatibility)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, "/tmp", $"/tmp/{name}", name)
        {
            ModelClass = template.ModelClass with
            {
                ID = modelClassId,
                Name = modelClassId,
                CompatClass = compatibility,
            },
        };
        handler.Models[model.Name] = model;
        return model;
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
        internal WanCatalogRegistry(params ResolvedVideoModel[] resolvedModels)
        {
            ResolvedModels = resolvedModels;
        }

        public IReadOnlyList<VideoArchitectureDescriptor> Catalog =>
            [WanArchitectureModule.Instance.Descriptor];

        public IReadOnlyList<ResolvedVideoModel> ResolvedModels { get; }

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
