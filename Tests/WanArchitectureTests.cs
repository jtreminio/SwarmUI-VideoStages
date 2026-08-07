using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Authoring;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

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
        T2IModelClass exactFourteen = fourteen.VideoModel.ModelClass;
        fourteen.VideoModel.ModelClass = exactFourteen with
        {
            CompatClass = T2IModelClassSorter.CompatWan22_5b,
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(
            fourteen.VideoModel,
            out _));
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
            ["first"],
            catalogModel["enhancements"]["referencePositions"].Values<string>());
        Assert.Equal(
            ["text-to-video", "image-to-video", "init-video"],
            catalogModel["capabilities"]["entryModes"].Values<string>());
        Assert.Equal(
            WanArchitectureModule.Ti2v5bModelClassId,
            catalogModel.Value<string>("modelClassId"));
        Assert.Equal(
            T2IModelClassSorter.CompatWan22_5b.ID,
            catalogModel.Value<string>("compatibilityClassId"));

        JObject architecture = Assert.Single(
            catalog["architectures"].Values<JObject>());
        Assert.Equal(
            WanArchitectureModule.ArchitectureId.Value,
            architecture.Value<string>("id"));
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

    [Fact]
    public void Catalog_model_capabilities_do_not_depend_on_a_stale_profile_hint()
    {
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor;
        ResolvedVideoModel resolved = TestResolvedVideoModel.Create(
            "wan-stale-profile.safetensors",
            new("removed-profile-alias"),
            descriptor);

        JObject catalog = ArchitectureCatalogSerializer.Serialize(
            new WanCatalogRegistry(resolved));
        JObject model = Assert.Single(catalog["models"].Values<JObject>());
        Assert.Equal(
            "removed-profile-alias",
            model.Value<string>("modelProfileId"));
        Assert.Equal(
            ["text-to-video", "image-to-video", "init-video"],
            model["capabilities"]["entryModes"].Values<string>());
        Assert.Equal(
            ["frameReferences"],
            model["capabilities"]["features"].Values<string>());
        Assert.Equal(WanArchitectureModule.FrameGrid, model.Value<int>("frameGrid"));
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

        Assert.Contains(ArchitectureEntryMode.ImageToVideo, descriptor.EntryModes);
        Assert.Contains(ArchitectureEntryMode.InitVideo, descriptor.EntryModes);
        Assert.True(descriptor.Features.HasFlag(ArchitectureFeature.FrameReferences));
        Assert.True(descriptor.StageGuideReferences.Allows(
            StageGuideReference.Classify("Generated")));
        Assert.True(descriptor.StageGuideReferences.Allows(
            StageGuideReference.Classify("PreviousStage")));
    }

    [Theory]
    [InlineData("pixel-lanczos", nameof(StageUpscaleMode.Pixel), "lanczos")]
    [InlineData(
        "model-fake-upscaler.safetensors",
        nameof(StageUpscaleMode.Model),
        "fake-upscaler.safetensors")]
    public void Effective_request_keeps_every_upscaler_wan_supports(
        string method,
        string expectedMode,
        string expectedMethodName)
    {
        VideoExecutionPlan plan = Compile(GeneratedClip(
            0,
            Stage(10, "wan-model") with { Upscale = 2, UpscaleMethod = method }));

        StageUpscalePlan upscale = Assert.Single(Assert.Single(plan.Clips).Stages).Core.Upscale;
        Assert.Equal(expectedMode, upscale.Mode.ToString());
        Assert.Equal(2, upscale.Factor);
        // The mode prefix is consumed by the classifier; the name is what the graph uses.
        Assert.Equal(expectedMethodName, upscale.MethodName);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code.Contains("upscale"));
    }

    [Fact]
    public void Effective_request_ignores_every_capability_wan_does_not_declare()
    {
        StageSpec stage = Stage(10, "wan-model");
        (string Code, ClipSpec Clip)[] cases =
        [
            ("effective-request.unsupported-retake-ignored",
                GeneratedClip(0, stage with { RetakeWindow = new(0, 8, 1) })),
            ("effective-request.wan-frame-reference-source-ignored",
                GeneratedClip(0, stage) with { FrameRefs = [new("Generated", 1, false, null)] }),
            ("effective-request.unsupported-prompt-relay-ignored",
                GeneratedClip(0, stage) with { PromptWindows = [new("late", 1, 1)] }),
            ("effective-request.unsupported-audio-reuse-ignored",
                GeneratedClip(0, stage) with { ReuseAudio = true }),
            ("effective-request.unsupported-audio-derived-duration-ignored",
                GeneratedClip(0, stage) with { ClipLengthFromAudio = true }),
            ("effective-request.unsupported-ic-lora-ignored",
                GeneratedClip(0, stage) with { ClipLengthFromControlNet = true }),
            ("effective-request.unsupported-stage-reference-ignored",
                GeneratedClip(0, stage with { ImageReference = "Base" })),
            ("effective-request.unsupported-ic-lora-ignored",
                GeneratedClip(0, stage) with
                {
                    IcLoras = [new("wan-ic.safetensors", "Upload", 1, 1, "canny", null)],
                }),
            ("effective-request.unsupported-audio-source-ignored",
                GeneratedClip(0, stage) with
                {
                    AudioSource = MediaSource.Upload,
                    UploadedAudio = new("data:audio/wav;base64,AA==", "voice.wav"),
                }),
        ];

        Assert.All(cases, entry => AssertIgnored(entry.Clip, entry.Code));
    }

    [Fact]
    public void Effective_request_keeps_one_uploaded_first_and_last_reference_and_warns_for_the_rest()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            FrameRefs =
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
            FrameRefs = [new("Upload", 1, true, "last.png", "last")],
        };
        TimelineSpec spec = new(512, 512, 24, false, [clip]);
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
            FrameRefs = [new("Base", 1, false, null)],
        };
        TimelineSpec spec = new(512, 512, 24, true, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(ArchitectureEntryMode.TextToVideo, compiled.EntryMode);
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
            FrameRefs =
            [
                new(
                    "Upload",
                    1,
                    false,
                    "first.png",
                    "data:image/png;base64,RklSU1Q="),
            ],
        };
        TimelineSpec spec = new(512, 512, 24, true, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(ArchitectureEntryMode.TextToVideo, compiled.EntryMode);
        Assert.Equal(StageInputKind.EmptyLatent, Assert.Single(compiled.Stages).Input);
        Assert.NotNull(compiled.RequireWanPayload().FirstFrameReference);
    }

    [Fact]
    public void First_reference_permission_comes_from_the_first_effective_model()
    {
        StageSpec stage = Stage(10, "wan-last-only");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            FrameRefs =
            [
                new(
                    "Upload",
                    1,
                    false,
                    "first.png",
                    "data:image/png;base64,RklSU1Q="),
            ],
        };
        TimelineSpec spec = new(512, 512, 24, true, [clip]);
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
        Assert.Equal(ArchitectureEntryMode.TextToVideo, compiled.EntryMode);
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
            FrameRefs = [new("Upload", 1, true, "last.png", "last")],
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
    public void Unsupported_latent_tail_does_not_steal_Wan_terminal_reference()
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
            FrameRefs = [new("Upload", 1, true, "last.png", "last")],
        };
        TimelineSpec spec = new(512, 512, 24, false, [clip]);

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

        Assert.NotNull(Assert.Single(plan.Clips).RequireWanPayload().LastFrameReference);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-last-frame-reference-ignored");
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-latent-upscale-ignored");
    }

    [Fact]
    public void Middle_frame_reference_is_dropped_with_a_warning()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            FrameRefs = [new("Upload", 3, false, "middle.png", "middle")],
        };

        VideoExecutionPlan plan = Compile(clip);
        WanClipPayload payload = Assert.Single(plan.Clips).RequireWanPayload();

        Assert.Null(payload.FirstFrameReference);
        Assert.Null(payload.LastFrameReference);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-middle-frame-reference-ignored"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning
                && diagnostic.ClipId == clip.Id);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Init_video_entry_drops_a_first_frame_reference_with_a_warning()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = InitVideoClip(0, stage) with
        {
            FrameRefs = [new("Upload", 1, false, "first.png", "first")],
        };

        VideoExecutionPlan plan = Compile(clip);

        Assert.Null(Assert.Single(plan.Clips).RequireWanPayload().FirstFrameReference);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-init-video-first-frame-reference-ignored"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning
                && diagnostic.ClipId == clip.Id);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Custom_frame_reference_strengths_are_reported_as_unused()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(
            0,
            stage with { FrameRefStrengths = [Constants.DefaultStageRefStrength / 2] }) with
        {
            FrameRefs = [new("Upload", 1, false, "first.png", "first")],
        };

        VideoExecutionPlan plan = Compile(clip);

        Assert.NotNull(Assert.Single(plan.Clips).RequireWanPayload().FirstFrameReference);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-reference-strengths-ignored"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning
                && diagnostic.ClipId == clip.Id);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void Default_frame_reference_strengths_are_not_reported()
    {
        StageSpec stage = Stage(10, "wan-model");
        ClipSpec clip = GeneratedClip(
            0,
            stage with { FrameRefStrengths = [Constants.DefaultStageRefStrength] }) with
        {
            FrameRefs = [new("Upload", 1, false, "first.png", "first")],
        };

        Assert.DoesNotContain(
            Compile(clip).Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-reference-strengths-ignored");
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
        StockHostVideoStagePayload firstPayload = compiled.Stages[0]
            .RequireStockHostVideoPayload(WanArchitectureModule.ArchitectureId, "Wan");
        StockHostVideoStagePayload secondPayload = compiled.Stages[1]
            .RequireStockHostVideoPayload(WanArchitectureModule.ArchitectureId, "Wan");
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
            .RequireStockHostVideoPayload(WanArchitectureModule.ArchitectureId, "Wan");

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
        TimelineSpec spec = new(
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
    public void Normal_LoRAs_target_the_model_only_across_every_Wan_compatibility_class()
    {
        Assert.All(
            (T2IModelCompatClass[])
            [
                T2IModelClassSorter.CompatWan21,
                T2IModelClassSorter.CompatWan21_1_3b,
                T2IModelClassSorter.CompatWan21_14b,
                T2IModelClassSorter.CompatWan22_5b,
            ],
            compatibility => Assert.False(compatibility.LorasTargetTextEnc));

        StageSpec stage = Stage(10, "wan-model") with
        {
            Loras = [new("stage-text-only", 0, 0.8), new("stage-model", 0.5, 0.9)],
        };
        StockHostVideoStagePayload payload = Assert.Single(
            Assert.Single(Compile(GeneratedClip(0, stage)).Clips).Stages)
            .RequireStockHostVideoPayload(WanArchitectureModule.ArchitectureId, "Wan");

        Assert.Equal(LoraTarget.ModelOnly, payload.LoraTarget);
        Assert.Equal("stage-model", Assert.Single(payload.Core.Loras).Name);
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
            Assert.Single(Compile(clip).Clips).Stages)
            .RequireStockHostVideoPayload(WanArchitectureModule.ArchitectureId, "Wan");

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
        TimelineSpec fiveSpec = new(512, 512, 24, false, [fiveClip]);
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
                stage.RequireStockHostVideoPayload(
                    WanArchitectureModule.ArchitectureId,
                    "Wan").ModelClassId));

        TimelineSpec cutSpec = new(
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

        ArchitectureClipCompilation compiled = CompileDirect(
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
            compiled.StagePayloads
                .OrderBy(pair => pair.Key)
                .Select(pair =>
                    Assert.IsType<StockHostVideoStagePayload>(pair.Value).ModelClassId));
    }

    [Theory]
    [InlineData(0.35, 7)]
    [InlineData(0.5, 6)]
    [InlineData(0.8, 2)]
    public void High_to_low_noise_pair_compiles_as_one_sampling_run(
        double control,
        int splitStep)
    {
        StageSpec high = Stage(
            10,
            "Wan2.2-I2V-A14B-HighNoise.safetensors");
        StageSpec low = Stage(
            11,
            "Wan2.2-I2V-A14B-LowNoise.safetensors") with
        {
            Control = control,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };

        ArchitectureClipCompilation compiled = CompileDirect(
            GeneratedClip(0, high, low));
        StockHostVideoStagePayload first = Assert.IsType<StockHostVideoStagePayload>(
            compiled.StagePayloads[0]);
        StockHostVideoStagePayload second = Assert.IsType<StockHostVideoStagePayload>(
            compiled.StagePayloads[1]);

        Assert.False(first.ContinuesSamplingFromPreviousStage);
        Assert.True(second.ContinuesSamplingFromPreviousStage);
        Assert.Equal(
            splitStep,
            StageStartStepPolicy.StartStep(
                second.Core.Steps,
                second.Core.Control));
    }

    // Sampler is intentionally absent because the continuation rule does not compare it.
    [Theory]
    [InlineData("Wan2.2-I2V-A14B-HighNoise.safetensors", 12, "normal", 0.5)]
    [InlineData("Wan2.2-I2V-A14B-LowNoise.safetensors", 10, "normal", 0.5)]
    [InlineData("Wan2.2-I2V-A14B-LowNoise.safetensors", 12, "karras", 0.5)]
    [InlineData("Wan2.2-I2V-A14B-LowNoise.safetensors", 12, "normal", 1)]
    public void Sampling_continuation_fails_closed_without_role_order_and_a_shared_schedule(
        string nextModel,
        int nextSteps,
        string nextScheduler,
        double nextControl)
    {
        StageSpec high = Stage(
            10,
            "Wan2.2-I2V-A14B-HighNoise.safetensors");
        StageSpec next = Stage(11, nextModel) with
        {
            Control = nextControl,
            Steps = nextSteps,
            Scheduler = nextScheduler,
            ImageReference = "PreviousStage",
            ClipStageIndex = 1,
            ClipStageRawIndex = 1,
        };

        ArchitectureClipCompilation compiled = CompileDirect(
            GeneratedClip(0, high, next));

        Assert.False(Assert.IsType<StockHostVideoStagePayload>(
            compiled.StagePayloads[1]).ContinuesSamplingFromPreviousStage);
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
        TimelineSpec spec = new(512, 512, 24, false, [clip]);
        VideoArchitectureRegistry registry = new(
        [
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
            Assert.Single(compiled.Stages).ResolvedModel.CompatibilityClassId);
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
        TimelineSpec spec = new(512, 512, 24, false, [clip]);
        VideoArchitectureRegistry registry = new(
        [
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
        TimelineSpec spec = new(
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
        Assert.Equal(StageInputKind.EmptyLatent, Assert.Single(compiled.Stages).Input);
        Assert.Equal(
            WanArchitectureModule.Ti2v5bModelClassId,
            Assert.Single(compiled.Stages)
                .RequireStockHostVideoPayload(
                    WanArchitectureModule.ArchitectureId,
                    "Wan")
                .ModelClassId);
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
        TimelineSpec spec = new(512, 512, 24, true, [clip]);
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
        TimelineSpec spec = new(512, 512, 24, true, [clip]);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));

        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        ClipPlan compiled = Assert.Single(plan.Clips);
        Assert.Equal(ArchitectureEntryMode.TextToVideo, compiled.EntryMode);
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
            T2IModelClassSorter.CompatWan21_14b.ID);

        ArchitectureClipCompilation compilation = WanClipPlanCompiler.Compile(
            GeneratedClip(0, stage),
            new Dictionary<int, ResolvedVideoModel>
            {
                [stage.ClipStageRawIndex] = resolved,
            },
            new(512, 512, 24, ArchitectureEntryMode.ImageToVideo));

        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Equal(
            resolved.ModelClassId,
            Assert.IsType<StockHostVideoStagePayload>(
                Assert.Single(compilation.StagePayloads).Value).ModelClassId);
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
    public void Compilation_keeps_effective_LoRAs_on_samplerless_passthrough()
    {
        StageSpec passthrough = Stage(10, "wan-model") with { Control = 0 };

        VideoExecutionPlan active = Compile(
            InitVideoClip(0, passthrough) with
            {
                Loras = [new("clip-active", 0.5)],
            });
        Assert.DoesNotContain(
            active.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
        Assert.Single(Assert.Single(active.Clips).Stages[0].Core.Loras);

        VideoExecutionPlan textOnly = Compile(
            InitVideoClip(
                0,
                passthrough with
                {
                    Loras = [new("stage-text-only", 0, 0.8)],
                }));
        Assert.Empty(
            Assert.Single(textOnly.Clips).Stages[0].Core.Loras);

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
        Assert.Empty(
            Assert.Single(stageNoOp.Clips).Stages[0].Core.Loras);

        VideoExecutionPlan sampled = Compile(
            InitVideoClip(0, passthrough with { Control = 0.5 }) with
            {
                Loras = [new("clip-active", 0.5)],
            });
        Assert.Single(
            Assert.Single(sampled.Clips).Stages[0].Core.Loras);
    }

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
        AssertWarnedAndNormalized(
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
    public void Schedule_policy_preserves_one_step_partial_boundary()
    {
        Assert.True(StageStartStepPolicy.PartialControlRoundsToZero(8, 0.9));
        Assert.False(StageStartStepPolicy.PartialControlRoundsToZero(8, 0.87));
        Assert.Equal(1, StageStartStepPolicy.StartStep(8, 0.87));
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

    private static void AssertWarnedAndNormalized(ClipSpec clip, string expectedOption)
    {
        VideoExecutionPlan plan = Compile(clip);
        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == "wan22.option.unsupported"
                && item.Severity == PlanDiagnosticSeverity.Warning
                && item.Message.Contains(expectedOption));
        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Severity == PlanDiagnosticSeverity.Error);
        Assert.NotNull(Assert.Single(plan.Clips).ArchitecturePayload);
    }

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
        TimelineSpec spec = new(512, 512, 24, false, [clip]);
        return VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));
    }

    private static ArchitectureClipCompilation CompileDirect(
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
        TimelineSpec spec,
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
            referencePositions ?? (five ? ["first"] : ["first", "last"]));
    }

    private static T2IModel AddWanModel(
        T2IModel template,
        string name,
        string modelClassId,
        T2IModelCompatClass compatibility)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, name), name)
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
            MediaSource.Native,
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
        SpecFixtures.Stage(id, model);

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
