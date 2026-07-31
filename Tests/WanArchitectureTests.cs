using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.HostVideo;
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
        Assert.Equal(WanArchitectureModule.FrameGrid, catalogModel.Value<int>("frameGrid"));
        Assert.Equal(
            ["text", "image"],
            catalogModel["entryAbilities"].Values<string>());
        Assert.Equal(
            ["first"],
            catalogModel["enhancements"]["referencePositions"].Values<string>());
        Assert.Equal(
            ["text-to-video", "image-to-video", "init-video"],
            catalogModel["capabilities"]["entryModes"].Values<string>());
        Assert.Null(catalogModel["entryModes"]);
        Assert.Null(catalogModel["enhancements"]["extras"]);

        JObject architecture = Assert.Single(
            catalog["architectures"].Values<JObject>());
        Assert.NotNull(architecture["capabilities"]);
        Assert.Null(architecture["extras"]);
        Assert.Null(architecture["profiles"]);
    }

    [Fact]
    public void Catalog_model_capabilities_do_not_depend_on_a_stale_profile_hint()
    {
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor;
        ResolvedVideoModel resolved = TestResolvedVideoModel.Create(
            "wan-stale-profile.safetensors",
            new("removed-profile-alias"),
            descriptor,
            entryAbilities:
                VideoModelEntryAbility.TextToVideo
                | VideoModelEntryAbility.ImageToVideo);

        JObject catalog = ArchitectureCatalogSerializer.Serialize(
            new WanCatalogRegistry(resolved));
        JObject model = Assert.Single(catalog["models"].Values<JObject>());
        Assert.Equal(
            "removed-profile-alias",
            model.Value<string>("modelProfileId"));
        Assert.Contains("lora", model["capabilities"]["stage"].Values<string>());
        Assert.Null(model["enhancements"]["extras"]);
    }

    [Fact]
    public void Boundary_policy_is_cut_only_on_the_Wan_frame_grid()
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;

        Assert.Equal(
            RuleSupport.Supported,
            descriptor.BoundaryRules[BoundaryJoinType.Cut].Support);
        Assert.Equal(
            RuleSupport.Unsupported,
            descriptor.BoundaryRules[BoundaryJoinType.Continue].Support);
        Assert.Equal(
            RuleSupport.Unsupported,
            descriptor.BoundaryRules[BoundaryJoinType.Crossfade].Support);
        Assert.Equal(WanArchitectureModule.FrameGrid, descriptor.FrameGrid);
    }

    [Fact]
    public void Descriptor_advertises_same_clip_stage_chaining_inputs()
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;

        Assert.True(descriptor.Capabilities.Architecture.HasFlag(
            ArchitectureCapability.InitVideoEntry));
        Assert.True(descriptor.Capabilities.Clip.HasFlag(ClipCapability.InitVideo));
        Assert.Contains(ArchitectureEntryMode.InitVideo, descriptor.EntryModes);
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.ImageInput));
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.VideoInput));
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.PixelUpscale));
        Assert.True(descriptor.Capabilities.Stage.HasFlag(StageCapability.Lora));
        Assert.True(descriptor.Capabilities.Stage.HasFlag(
            StageCapability.FrameReferences));
        Assert.True(descriptor.Capabilities.Clip.HasFlag(ClipCapability.References));
        Assert.Contains(
            HostVideoStageRules.NormalLoraRequiresSamplingStage,
            descriptor.Rules);
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
            "effective-request.unsupported-upscale-ignored");
        AssertIgnored(
            GeneratedClip(0, stage with { RetakeWindow = new(0, 8, 1) }),
            "effective-request.unsupported-retake-ignored");
        AssertIgnored(
            GeneratedClip(0, stage) with { ImageRefs = [new("Generated", 1, false, null)] },
            "effective-request.wan-frame-reference-source-ignored");
        AssertIgnored(
            GeneratedClip(0, stage) with { PromptWindows = [new("late", 1, 1)] },
            "effective-request.unsupported-prompt-relay-ignored");
        AssertIgnored(
            GeneratedClip(0, stage) with { ReuseAudio = true },
            "effective-request.unsupported-audio-reuse-ignored");
        AssertIgnored(
            GeneratedClip(0, stage) with { ClipLengthFromAudio = true },
            "effective-request.unsupported-audio-derived-duration-ignored");
        AssertIgnored(
            GeneratedClip(0, stage) with { ClipLengthFromControlNet = true },
            "effective-request.unsupported-control-signal-derived-duration-ignored");
        AssertIgnored(
            GeneratedClip(0, stage with { ImageReference = "Base" }),
            "effective-request.unsupported-stage-reference-ignored");
        AssertIgnored(
            GeneratedClip(0, stage) with
            {
                IcLoras = [new("wan-ic.safetensors", "Upload", 1, 1, "canny", null)],
            },
            "effective-request.unsupported-ic-lora-ignored");
    }

    [Fact]
    public void Effective_request_keeps_one_uploaded_first_and_last_reference_and_warns_for_the_rest()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ImageRefs =
            [
                new("Upload", 3, false, "middle.png", "middle"),
                new("Upload", 1, false, "first.png", "first"),
                new("Upload", 1, false, "duplicate.png", "duplicate"),
                new("Base", 1, true, null),
                new("Upload", 1, true, "last.png", "last"),
            ],
        };

        VideoExecutionPlan plan = Compile(clip);
        WanClipPayload payload = Assert.Single(plan.Clips).RequireWanPayload();

        Assert.Equal("first.png", payload.FirstFrameReference.UploadFileName);
        Assert.Equal("last.png", payload.LastFrameReference.UploadFileName);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-middle-frame-reference-ignored");
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-duplicate-frame-reference-ignored");
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-frame-reference-source-ignored");
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Effective_request_ignores_last_reference_when_the_terminal_model_lacks_it()
    {
        StageSpec stage = Stage(10, "wan-five");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ImageRefs = [new("Upload", 1, true, "last.png", "last")],
        };
        VideoStagesSpec spec = new(512, 512, 24, false, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(
                spec,
                new Dictionary<string, ModelProfileId>
                {
                    [stage.Model] = WanArchitectureModule.Ti2v5bProfileId,
                }));

        Assert.Null(Assert.Single(plan.Clips).RequireWanPayload().LastFrameReference);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-last-frame-reference-ignored"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Ignored_non_upload_first_reference_does_not_change_text_entry()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ImageRefs = [new("Base", 1, false, null)],
        };
        VideoStagesSpec spec = new(512, 512, 24, true, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(ClipInputKind.EmptyLatent, compiled.Input);
        Assert.Equal(StageInputKind.EmptyLatent, Assert.Single(compiled.Stages).Input);
        Assert.Null(compiled.RequireWanPayload().FirstFrameReference);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-frame-reference-source-ignored"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Uploaded_first_reference_keeps_the_text_root_plan_shape()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ImageRefs =
            [
                new(
                    "Upload",
                    1,
                    false,
                    "first.png",
                    "data:image/png;base64,RklSU1Q="),
            ],
        };
        VideoStagesSpec spec = new(512, 512, 24, true, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(ClipInputKind.EmptyLatent, compiled.Input);
        Assert.Equal(StageInputKind.EmptyLatent, Assert.Single(compiled.Stages).Input);
        Assert.NotNull(compiled.RequireWanPayload().FirstFrameReference);
    }

    [Fact]
    public void First_reference_permission_comes_from_the_first_effective_model()
    {
        StageSpec stage = Stage(10, "wan-last-only");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ImageRefs =
            [
                new(
                    "Upload",
                    1,
                    false,
                    "first.png",
                    "data:image/png;base64,RklSU1Q="),
            ],
        };
        VideoStagesSpec spec = new(512, 512, 24, true, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(
                spec,
                referencePositionsByModel: new Dictionary<string, IReadOnlyList<string>>
                {
                    [stage.Model] = ["last"],
                }));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Null(compiled.RequireWanPayload().FirstFrameReference);
        Assert.Equal(ClipInputKind.EmptyLatent, compiled.Input);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-first-frame-reference-ignored"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Effective_request_uses_the_terminal_active_stage_before_a_skipped_tail()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            ImageRefs = [new("Upload", 1, true, "last.png", "last")],
            AuthoredStages =
            [
                new(0, stage.Model, null, false),
                new(1, stage.Model, null, true),
            ],
        };

        VideoExecutionPlan plan = Compile(clip);

        Assert.NotNull(Assert.Single(plan.Clips).RequireWanPayload().LastFrameReference);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-last-frame-reference-ignored");
    }

    [Fact]
    public void Effective_request_checks_Wan_terminal_reference_before_ignoring_latent_upscale()
    {
        StageSpec first = Stage(10, "wan-last") with
        {
            ClipStageIndex = 0,
            ClipStageRawIndex = 0,
        };
        StageSpec latentTail = Stage(11, "wan-first") with
        {
            Control = 0,
            Upscale = 2,
            UpscaleMethod = "latent-detail",
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };
        ClipSpec clip = GeneratedClip(0, first, latentTail) with
        {
            ImageRefs = [new("Upload", 1, true, "last.png", "last")],
        };
        VideoStagesSpec spec = new(512, 512, 24, false, [clip]);

        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(
                spec,
                referencePositionsByModel:
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        [first.Model] = ["first", "last"],
                        [latentTail.Model] = ["first"],
                    }));

        Assert.Null(Assert.Single(plan.Clips).RequireWanPayload().LastFrameReference);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-last-frame-reference-ignored");
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-upscale-ignored");
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
        StockHostVideoStagePayload firstPayload = compiled.Stages[0].RequireWanPayload();
        StockHostVideoStagePayload secondPayload = compiled.Stages[1].RequireWanPayload();
        Assert.Equal("wan-model", compiled.Stages[0].ResolvedModel.ModelName);
        Assert.Equal(1, firstPayload.Core.Control);
        Assert.Equal(12, firstPayload.Core.Steps);
        Assert.Equal(4.5, firstPayload.Core.CfgScale);
        Assert.Equal("euler", firstPayload.Core.Sampler);
        Assert.Equal("normal", firstPayload.Core.Scheduler);
        Assert.Equal(StageUpscaleMode.None, firstPayload.Core.Upscale.Mode);
        Assert.Equal(1, firstPayload.Core.Upscale.Factor);
        Assert.Equal(0.35, secondPayload.Core.Control);
        Assert.Equal(17, secondPayload.Core.Steps);
        Assert.Empty(firstPayload.Core.Loras);
        Assert.Empty(secondPayload.Core.Loras);
    }

    [Fact]
    public void Compilation_carries_a_normalized_Wan_pixel_upscale()
    {
        StageSpec stage = Stage(10, "wan-model") with
        {
            Upscale = 1.5,
            UpscaleMethod = "pixel-bicubic",
        };

        StockHostVideoStagePayload payload = Assert.Single(
            Assert.Single(Compile(GeneratedClip(0, stage)).Clips).Stages)
            .RequireWanPayload();

        Assert.Equal(StageUpscaleMode.Pixel, payload.Core.Upscale.Mode);
        Assert.Equal(1.5, payload.Core.Upscale.Factor);
        Assert.Equal("pixel-bicubic", payload.Core.Upscale.RawMethod);
        Assert.Equal("bicubic", payload.Core.Upscale.MethodName);
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
    public void Catalog_advertises_normal_LoRA_support_and_architecture_rule()
    {
        JObject catalog = ArchitectureCatalogSerializer.Serialize(new WanCatalogRegistry());
        JObject wan = Assert.Single(
            catalog["architectures"].Values<JObject>(),
            item => item.Value<string>("id") == "wan22");
        Assert.Contains("lora", wan["capabilities"]["stage"].Values<string>());
        Assert.Null(wan["profiles"]);
        JObject rule = Assert.Single(wan["rules"].Values<JObject>());
        Assert.Equal(
            HostVideoStageRules.NormalLoraRequiresSamplingStageCode,
            rule.Value<string>("code"));
        Assert.Equal("conditional", rule.Value<string>("support"));
        Assert.Equal("stage", rule.Value<string>("scope"));
        Assert.Equal(
            0,
            rule["constraints"].Value<double>("exclusiveMinimumControl"));
        Assert.Equal(
            0,
            HostVideoStageRules.NormalLoraRequiresSamplingStage
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

        StockHostVideoStagePayload payload = Assert.Single(
            Assert.Single(Compile(clip).Clips).Stages).RequireWanPayload();

        Assert.Collection(
            payload.Core.Loras,
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
    public void Compilation_accepts_bounded_init_video_stage_zero_and_previous_stage_chaining()
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
            Compile(InitVideoClip(0, first, second)).Clips);

        Assert.Equal(ArchitectureEntryMode.InitVideo, compiled.EntryMode);
        Assert.Equal(ClipInputKind.InitVideo, compiled.Input);
        Assert.Equal(StageInputKind.InitVideo, compiled.Stages[0].Input);
        Assert.Equal(StageInputKind.PreviousStage, compiled.Stages[1].Input);
        Assert.Equal(0.5, compiled.Stages[0].Core.Control);
        Assert.Equal(1, compiled.Stages[1].Core.Control);
    }

    [Fact]
    public void Resolved_clips_publish_their_compatibility_class_and_allow_cut_clips_to_differ()
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
        Assert.All(
            compiledFive.Stages,
            stage => Assert.Equal(
                WanArchitectureModule.Ti2v5bModelClassId,
                stage.RequireWanPayload().ModelClassId));

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
        Assert.Equal(2, cutPlan.Clips.Count);
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
                WanArchitectureModule.ImageToVideoModelClassId,
                "wan-2_1-image2video-14b",
            ],
            compiled.Stages
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.ModelClassId));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolution_rejects_an_authored_Wan_stage_from_another_compatibility_class(
        bool skipped)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle installed = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel other = AddWanModel(
            installed.VideoModel,
            "UnitTest_Wan21_I2V_1_3B.safetensors",
            "wan-2_1-image2video-1_3b",
            T2IModelClassSorter.CompatWan21_1_3b);
        StageSpec first = Stage(10, installed.VideoModel.Name);
        StageSpec second = Stage(11, other.Name) with
        {
            Control = 0.5,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };
        ClipSpec clip = GeneratedClip(
            0,
            skipped ? [first] : [first, second]) with
        {
            AuthoredStages =
            [
                new(0, installed.VideoModel.Name, null, false),
                new(1, other.Name, null, skipped),
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
        Assert.Equal(
            skipped,
            diagnostic.Message.Contains("(skipped)", StringComparison.Ordinal));
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
            WanArchitectureModule.Ti2v5bModelClassId,
            Assert.Single(compiled.Stages).RequireWanPayload().ModelClassId);
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
        ResolvedVideoModel resolved = TestResolvedVideoModel.Create(
            "canonical-wan-model.safetensors",
            WanArchitectureModule.ImageToVideoProfileId,
            descriptor,
            WanArchitectureModule.ImageToVideoModelClassId,
            T2IModelClassSorter.CompatWan21_14b.ID,
            VideoModelEntryAbility.ImageToVideo);

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
            resolved.ModelClassId,
            Assert.Single(compilation.Stages).Value.ModelClassId);
    }

    [Fact]
    public void Capability_validation_rejects_a_missing_stage_resolution_before_Wan_compilation()
    {
        StageSpec stage = Stage(10, "authored-wan-model");
        ClipSpec clip = GeneratedClip(0, stage);
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;

        IReadOnlyList<PlanDiagnostic> diagnostics =
            ArchitectureCapabilityValidator.Validate(
                clip,
                descriptor,
                ArchitectureEntryMode.ImageToVideo,
                new Dictionary<int, ResolvedVideoModel>
                {
                    [stage.ClipStageRawIndex] = null,
                });

        PlanDiagnostic diagnostic = Assert.Single(
            diagnostics,
            item => item.Code == "architecture-model-entry-unsupported");
        Assert.Equal(PlanDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(stage.Id, diagnostic.StageId);
        Assert.Equal(stage.ClipStageRawIndex, diagnostic.RawStageIndex);
        Assert.Contains(stage.Model, diagnostic.Message);
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
            Compile(InitVideoClip(0, first, second)).Clips);

        Assert.All(compiled.Stages, stage => Assert.True(stage.IsPassthrough));
        Assert.Equal(StageInputKind.InitVideo, compiled.Stages[0].Input);
        Assert.Equal(StageInputKind.PreviousStage, compiled.Stages[1].Input);
        Assert.All(
            compiled.Stages,
            stage => Assert.Equal(0, stage.Core.Control));
    }

    [Fact]
    public void Compilation_refuses_effective_LoRAs_on_samplerless_passthrough()
    {
        StageSpec passthrough = Stage(10, "wan-model") with { Control = 0 };

        AssertBlocked(
            Compile(
                InitVideoClip(0, passthrough) with
                {
                    Loras = [new("clip-active", 0.5)],
                }),
            HostVideoStageRules.NormalLoraRequiresSamplingStageCode,
            HostVideoStageRules.NormalLoraRequiresSamplingStageReason);
        VideoExecutionPlan textOnly = Compile(
            InitVideoClip(
                0,
                passthrough with
                {
                    Loras = [new("stage-text-only", 0, 0.8)],
                }));
        Assert.DoesNotContain(
            textOnly.Diagnostics,
            diagnostic =>
                diagnostic.Code
                    == HostVideoStageRules.NormalLoraRequiresSamplingStageCode);
        Assert.Empty(
            Assert.Single(textOnly.Clips).Stages[0].Core.Loras);
        AssertBlocked(
            Compile(
                InitVideoClip(
                    0,
                    passthrough with
                    {
                        Control = -0.1,
                        Loras = [new("stage-negative-control", 1)],
                    })),
            HostVideoStageRules.NormalLoraRequiresSamplingStageCode,
            HostVideoStageRules.NormalLoraRequiresSamplingStageReason);

        ClipSpec disabled = InitVideoClip(
            0,
            passthrough with { LoraWeights = [0] }) with
        {
            Loras = [new("clip-disabled", 1)],
        };
        ClipPlan compiled = Assert.Single(Compile(disabled).Clips);
        Assert.True(Assert.Single(compiled.Stages).IsPassthrough);
        Assert.Empty(compiled.Stages[0].Core.Loras);

        VideoExecutionPlan stageNoOp = Compile(
            InitVideoClip(
                0,
                passthrough with
                {
                    Loras = [new("stage-no-op", 0, 0)],
                }));
        Assert.DoesNotContain(
            stageNoOp.Diagnostics,
            diagnostic =>
                diagnostic.Code
                    == HostVideoStageRules.NormalLoraRequiresSamplingStageCode);
        Assert.Empty(
            Assert.Single(stageNoOp.Clips).Stages[0].Core.Loras);

        VideoExecutionPlan sampled = Compile(
            InitVideoClip(0, passthrough with { Control = 0.5 }) with
            {
                Loras = [new("clip-active", 0.5)],
            });
        Assert.DoesNotContain(
            sampled.Diagnostics,
            diagnostic =>
                diagnostic.Code
                    == HostVideoStageRules.NormalLoraRequiresSamplingStageCode);
        Assert.Single(
            Assert.Single(sampled.Clips).Stages[0].Core.Loras);
    }

    [Fact]
    public void Static_generated_frames_use_the_architecture_grid()
    {
        Assert.Equal(4, WanArchitectureModule.FrameGrid);
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor;
        ResolvedVideoModel resolved = TestResolvedVideoModel.Create(
            "synthetic-wan",
            new("stale-profile-alias"),
            descriptor);

        WanStaticGeneratedFrameResolution resolution =
            WanStaticGeneratedFrameResolver.Resolve(16, 2, 10, resolved);

        Assert.Equal(4, resolution.FrameGrid);
        Assert.Equal(13, resolution.Frames);
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

    /// <summary>Decoded scheduling rules are architecture-owned. Root control and guide
    /// canonicalization are intentionally owned by parsing/projection and are not repeated here.</summary>
    [Fact]
    public void Compiler_refuses_invalid_decoded_controls_and_quantized_schedules()
    {
        StageSpec stage = Stage(10, "wan-model");

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
                    Control = 0.9,
                    Steps = 8,
                    ImageReference = "PreviousStage",
                    ClipStageIndex = 1,
                    ClipStageRawIndex = 1,
                }),
            "quantizes to sampler start step 0");
        AssertRefused(
            InitVideoClip(0, stage with { Control = 1.1 }),
            "decoded-input control outside the finite range [0, 1]");
        AssertRefused(
            InitVideoClip(0, stage with { Control = double.NaN }),
            "decoded-input control outside the finite range [0, 1]");
        AssertRefused(
            InitVideoClip(0, stage with { Control = 0.9, Steps = 8 }),
            "quantizes to sampler start step 0");
    }

    [Fact]
    public void Compiler_ignores_forged_profile_alias_when_model_facts_are_valid()
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
        Dictionary<int, ResolvedVideoModel> stageModels = new()
        {
            [0] = TestResolvedVideoModel.Create(
                first.Model,
                new("forged-first"),
                canonical,
                WanArchitectureModule.ImageToVideoModelClassId,
                T2IModelClassSorter.CompatWan21_14b.ID),
            [1] = TestResolvedVideoModel.Create(
                second.Model,
                alternateId,
                canonical,
                WanArchitectureModule.ImageToVideoModelClassId,
                T2IModelClassSorter.CompatWan21_14b.ID),
        };

        ArchitectureClipCompilation compilation =
            WanArchitectureModule.Instance.ValidateAndCompileClip(
                clip,
                stageModels,
                new(512, 512, 24));

        Assert.DoesNotContain(
            compilation.Diagnostics,
            item => item.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Schedule_policy_preserves_one_step_partial_boundary()
    {
        Assert.True(HostVideoStageSchedulePolicy.IsQuantizedZeroPartial(8, 0.9));
        Assert.False(HostVideoStageSchedulePolicy.IsQuantizedZeroPartial(8, 0.87));
        Assert.Equal(1, HostVideoStageSchedulePolicy.StartStep(8, 0.87));
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
        IReadOnlyDictionary<string, ModelProfileId> profilesByModel = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>
            referencePositionsByModel = null)
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        Dictionary<int, ClipArchitectureAssignment> clips = [];
        foreach (ClipSpec clip in spec.Clips)
        {
            Dictionary<int, ResolvedVideoModel> stageModels = [];
            foreach (StageSpec stage in clip.Stages ?? [])
            {
                IReadOnlyList<string> referencePositions = null;
                referencePositionsByModel?.TryGetValue(
                    stage.Model,
                    out referencePositions);
                ResolvedVideoModel resolved = ResolvedWan(
                    stage.Model,
                    profilesByModel is not null
                        && profilesByModel.TryGetValue(stage.Model, out ModelProfileId profileId)
                            ? profileId
                            : WanArchitectureModule.ImageToVideoProfileId,
                    descriptor,
                    referencePositions);
                stageModels[stage.ClipStageRawIndex] = resolved;
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
        VideoArchitectureDescriptor descriptor,
        IReadOnlyList<string> referencePositions = null)
    {
        bool five = profile == WanArchitectureModule.Ti2v5bProfileId;
        bool ordinary = profile == WanArchitectureModule.OrdinaryImageToVideoProfileId;
        return TestResolvedVideoModel.Create(
            model,
            profile,
            descriptor,
            five
                ? WanArchitectureModule.Ti2v5bModelClassId
                : ordinary
                    ? "wan-2_1-image2video-14b"
                    : WanArchitectureModule.ImageToVideoModelClassId,
            five
                ? T2IModelClassSorter.CompatWan22_5b.ID
                : T2IModelClassSorter.CompatWan21_14b.ID,
            VideoModelEntryAbility.TextToVideo
                | VideoModelEntryAbility.ImageToVideo,
            referencePositions ?? (five ? ["first"] : ["first", "last"]));
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

    private static ClipSpec InitVideoClip(int id, params StageSpec[] stages) =>
        GeneratedClip(id, stages) with
        {
            InitVideo = new("data", "source.mp4", 0),
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
