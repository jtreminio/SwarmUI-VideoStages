using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Wan;
using VideoStages.Authoring;
using VideoStages.Execution.Graph;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>Direct tests for WAN runtime failure recovery and unreachable states.</summary>
[Collection("VideoStagesTests")]
public class WanRuntimeFlowTests
{
    private const double WanInitVideoDuration = 0.6;
    private const double WanInitVideoStartSeconds = 1;
    private static readonly string[] WanSourceFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    [Fact]
    public void Legacy_Wan_swap_fields_are_preserved_and_warned_about()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel legacySwapModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Legacy_Wan_Swap.safetensors");
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoSwapModel, legacySwapModel);
        input.Set(T2IParamTypes.VideoSwapPercent, double.NaN);
        input.Set(T2IParamTypes.Steps, -17, T2IParamInput.SectionID_VideoSwap);
        input.Set(T2IParamTypes.CFGScale, 99, T2IParamInput.SectionID_VideoSwap);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.False(generator.IsImageToVideoSwap);
        Assert.Single(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.video-swap-ignored");
        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Single(
            warnings,
            warning => warning.Contains(
                "Create separate timeline stages",
                StringComparison.Ordinal));

        LegacyVideoSwapRequestSnapshot snapshot =
            generator.GetTimelineSpec().LegacyVideoSwap;
        Assert.Equal(legacySwapModel.Name, snapshot.VideoSwapModelName);
        Assert.True(snapshot.HasExplicitVideoSwapPercent);
        Assert.True(double.IsNaN(snapshot.ExplicitVideoSwapPercent!.Value));
        Assert.True(snapshot.HasVideoSwapSectionOverrides);
        Assert.Same(legacySwapModel, input.Get(T2IParamTypes.VideoSwapModel, null));
        Assert.True(double.IsNaN(input.Get(T2IParamTypes.VideoSwapPercent, 0.5)));
        Assert.Equal(
            -17,
            input.Get(
                T2IParamTypes.Steps,
                0,
                sectionId: T2IParamInput.SectionID_VideoSwap,
                includeBase: false));
        Assert.Equal(
            99,
            input.Get(
                T2IParamTypes.CFGScale,
                0,
                sectionId: T2IParamInput.SectionID_VideoSwap,
                includeBase: false));

        WorkflowGenerator.ImageToVideoGenInfo authoredStageInfo = new()
        {
            Generator = generator,
            ContextID = VideoStagesExtension.SectionIdForStage(0),
            VideoSwapModel = legacySwapModel,
            VideoSwapPercent = 0.42,
        };
        WanHostHandlers.IsolateCoreSettings(authoredStageInfo);
        Assert.Same(legacySwapModel, authoredStageInfo.VideoSwapModel);
        Assert.Equal(0.42, authoredStageInfo.VideoSwapPercent);

        WorkflowGenerator.ImageToVideoGenInfo customInfo = new()
        {
            Generator = generator,
            ContextID = 99123,
            VideoSwapModel = legacySwapModel,
            VideoSwapPercent = 0.31,
        };
        WanHostHandlers.IsolateCoreSettings(customInfo);
        Assert.Same(legacySwapModel, customInfo.VideoSwapModel);
        Assert.Equal(0.31, customInfo.VideoSwapPercent);

        Image endFrame = new([0x44], MediaType.ImagePng);
        input.Set(T2IParamTypes.VideoEndFrame, endFrame);
        WorkflowGenerator.ImageToVideoGenInfo coreInfo = new()
        {
            Generator = generator,
            ContextID = T2IParamInput.SectionID_Video,
            VideoEndFrame = endFrame,
        };
        InvalidOperationException completedError = Assert.Throws<InvalidOperationException>(
            () => WanHostHandlers.IsolateCoreSettings(coreInfo));
        Assert.Contains("Completed", completedError.Message);
        Assert.Same(endFrame, coreInfo.VideoEndFrame);
        Assert.Same(endFrame, input.Get(T2IParamTypes.VideoEndFrame, null));

        WorkflowGenerator.ImageToVideoGenInfo authoredEndFrameInfo = new()
        {
            Generator = generator,
            ContextID = VideoStagesExtension.SectionIdForStage(0),
            VideoEndFrame = endFrame,
        };
        WanHostHandlers.IsolateCoreSettings(authoredEndFrameInfo);
        Assert.Same(endFrame, authoredEndFrameInfo.VideoEndFrame);

        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Legacy_swap_isolation_does_not_touch_non_VideoStages_or_no_Wan_requests()
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        WanHostHandlers.Register();
        WanHostHandlers.Register();
        Assert.Single(
            WorkflowGenerator.AltImageToVideoPreHandlers,
            handler => handler == WanHostHandlers.IsolateCoreSettings);

        WorkflowGenerator inactive = new()
        {
            UserInput = new(null),
            Features = [],
            ModelFolderFormat = "/",
        };
        WorkflowGenerator.ImageToVideoGenInfo inactiveInfo = new()
        {
            Generator = inactive,
            VideoSwapModel = models.WanVideoModel,
            VideoSwapPercent = 0.73,
        };
        WanHostHandlers.IsolateCoreSettings(inactiveInfo);
        Assert.Same(models.WanVideoModel, inactiveInfo.VideoSwapModel);
        Assert.Equal(0.73, inactiveInfo.VideoSwapPercent);

        T2IParamInput ltxInput = BuildNativeInput(
            models.BaseModel,
            models.LtxVideoModel,
            JsonSingleClipStages(
                MakeStage(models.LtxVideoModel.Name, "Generated", steps: 8)));
        WorkflowGenerator ltxGenerator = new()
        {
            UserInput = ltxInput,
            Features = [],
            ModelFolderFormat = "/",
        };
        WorkflowGenerator.ImageToVideoGenInfo ltxInfo = new()
        {
            Generator = ltxGenerator,
            VideoSwapModel = models.WanVideoModel,
            VideoSwapPercent = 0.61,
        };
        WanHostHandlers.IsolateCoreSettings(ltxInfo);
        Assert.Same(models.WanVideoModel, ltxInfo.VideoSwapModel);
        Assert.Equal(0.61, ltxInfo.VideoSwapPercent);
    }

    [Fact]
    public void Wan_text_entry_rejects_a_malicious_host_latent_and_restores_scopes()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        const string modelClassId = "wan-2_1-text2video-malicious";
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
            Name = "WAN malicious empty-video test",
        };
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["duration"] = 13.0 / 24.0;
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeDocument(clip).ToString());

        WorkflowGenerator captured = null;
        WGNodeData ambientAudioVae = null;
        bool creatorCalled = false;
        WorkflowGenerator.WorkflowGenStep installAmbientState = new(g =>
        {
            captured = g;
            const string audioVaeId = "unit-malicious-text-ambient-audio-vae";
            g.Workflow[audioVaeId] = new JObject
            {
                ["class_type"] = "UnitTest_MaliciousTextAmbientAudioVae",
                ["inputs"] = new JObject(),
            };
            ambientAudioVae = new(
                new JArray(audioVaeId, 0),
                g,
                WGNodeData.DT_AUDIOVAE,
                null);
            g.CurrentAudioVae = ambientAudioVae;
            g.IsImageToVideo = false;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        bool hadPriorCreator = WorkflowGenerator.EmptyImageCreators.TryGetValue(
            modelClassId,
            out Func<int, int, int, string, WGNodeData> priorCreator);
        try
        {
            WorkflowGenerator.EmptyImageCreators[modelClassId] =
                (width, height, batchSize, id) =>
                {
                    creatorCalled = true;
                    Assert.NotNull(captured);
                    Assert.True(captured.IsImageToVideo);
                    Assert.Null(captured.CurrentAudioVae);
                    Assert.NotNull(captured.CurrentVae);
                    // 13 authored intervals plus the endpoint frame is 14, which WAN's 4k+1 grid
                    // snaps up to 17.
                    int scopedFrames = input.Get(T2IParamTypes.Text2VideoFrames);
                    Assert.Equal(17, scopedFrames);
                    string node = captured.CreateNode(
                        "EmptyHunyuanLatentVideo",
                        new JObject
                        {
                            ["batch_size"] = batchSize,
                            ["length"] = scopedFrames,
                            ["width"] = width,
                            ["height"] = height,
                        },
                        id);
                    return new(
                        new JArray(node, 0),
                        captured,
                        WGNodeData.DT_LATENT_IMAGE,
                        null)
                    {
                        Width = width,
                        Height = height,
                        Frames = scopedFrames,
                    };
                };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => WorkflowTestHarness.GenerateWithStepsAndState(
                    input,
                    WorkflowTestHarness.Template_BaseOnlyImage()
                        .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                        .Concat(WorkflowTestHarness.VideoStagesSteps())
                        .Append(installAmbientState)));

            Assert.Contains("could not create a valid", error.Message);
            Assert.True(creatorCalled);
            Assert.NotNull(captured);
            Assert.Equal(25, input.Get(T2IParamTypes.Text2VideoFrames));
            Assert.Null(captured.CurrentVae);
            Assert.Same(ambientAudioVae, captured.CurrentAudioVae);
            Assert.False(captured.IsImageToVideo);
            using WorkflowBridge bridge = WorkflowBridge.Create(captured.Workflow);
            Assert.Empty(SamplerNodes(bridge));
        }
        finally
        {
            if (hadPriorCreator)
            {
                WorkflowGenerator.EmptyImageCreators[modelClassId] = priorCreator;
            }
            else
            {
                WorkflowGenerator.EmptyImageCreators.Remove(modelClassId);
            }
        }
        Assert.Equal(
            hadPriorCreator,
            WorkflowGenerator.EmptyImageCreators.TryGetValue(
                modelClassId,
                out Func<int, int, int, string, WGNodeData> restoredCreator));
        if (hadPriorCreator)
        {
            Assert.Same(priorCreator, restoredCreator);
        }
    }

    [Fact]
    public void Wan5b_cleanup_uses_resolved_profile_despite_forged_authored_hints()
    {
        using SwarmUiTestContext context = new(clearModelGenSteps: false);
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        AddLoraModel("UnitTest_Wan5b_Failure_Prompt.safetensors");
        AddLoraModel("UnitTest_Wan5b_Failure_Persisted.safetensors");
        JObject clip = MakeWanInitVideoClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 12);
        JObject stage = (JObject)((JArray)clip["stages"])[0];
        clip["modelProfileId"] = "forged-non-5b-profile";
        stage["modelProfileId"] = "forged-non-5b-profile";
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan5b_Failure_Persisted",
            ["weight"] = 0.6,
            ["textEncoderWeight"] = 0.8,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString(),
            prompt:
                "global <videoclip[0,0]><lora:UnitTest_Wan5b_Failure_Prompt:0.4:0.7>");
        const string priorSourceId = "unit-prior-wan22-source";
        const string priorLatentId = "unit-prior-wan22-latent";
        const string priorConsumerId = "unit-prior-wan22-consumer";
        const string priorAudioVaeId = "unit-prior-audio-vae";
        const string priorCacheKey = "unit-prior-wan22-cache";
        WorkflowGenerator captured = null;
        WGNodeData ambientAudioVae = null;
        LoraParamState original = null;
        HashSet<string> wan22LatentsBeforeStage = null;
        string failedLatentId = null;
        string failedLatentStartImageId = null;
        string failedHostScaleId = null;
        string actualPreHostMediaId = null;
        JToken actualPreHostMediaDefinition = null;
        WorkflowGenerator.WorkflowGenStep installPriorState = new(g =>
        {
            captured = g;
            original = CaptureLoraParams(g.UserInput);
            g.Workflow[priorSourceId] = new JObject
            {
                ["class_type"] = "UnitTest_PriorWan22Source",
                ["inputs"] = new JObject(),
            };
            g.Workflow[priorLatentId] = new JObject
            {
                ["class_type"] = "Wan22ImageToVideoLatent",
                ["inputs"] = new JObject
                {
                    ["start_image"] = new JArray(priorSourceId, 0),
                },
            };
            g.Workflow[priorConsumerId] = new JObject
            {
                ["class_type"] = "UnitTest_PriorWan22Consumer",
                ["inputs"] = new JObject
                {
                    ["latent"] = new JArray(priorLatentId, 0),
                },
            };
            g.Workflow[priorAudioVaeId] = new JObject
            {
                ["class_type"] = "UnitTest_PriorAudioVae",
                ["inputs"] = new JObject(),
            };
            VideoGraphHelpers.CachePath(
                g,
                priorCacheKey,
                new JArray(priorLatentId, 0));
            ambientAudioVae = new(
                new JArray(priorAudioVaeId, 0),
                g,
                WGNodeData.DT_AUDIOVAE,
                null);
            g.CurrentAudioVae = ambientAudioVae;
            wan22LatentsBeforeStage = [
                .. g.Workflow.Properties()
                    .Where(property =>
                        property.Value["class_type"]?.ToString()
                            == "Wan22ImageToVideoLatent")
                    .Select(property => property.Name),
            ];
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);
        Action<WorkflowGenerator.ImageToVideoGenInfo> failAfterHostLatent = info =>
        {
            if (info.ContextID != VideoStagesExtension.SectionIdForStage(0))
            {
                return;
            }
            using WorkflowBridge bridge = WorkflowBridge.Create(info.Generator.Workflow);
            ComfyNode failedLatent = Assert.Single(
                NodesOfClass(bridge, "Wan22ImageToVideoLatent"),
                node => !wan22LatentsBeforeStage.Contains(node.Id));
            failedLatentId = failedLatent.Id;
            ComfyNode hostOwnedInput =
                failedLatent.FindInput("start_image").Connection?.Node;
            failedLatentStartImageId = hostOwnedInput?.Id;
            while (hostOwnedInput?.ClassTypeName == "ImageFromBatch")
            {
                hostOwnedInput = hostOwnedInput
                    .FindInput("image")
                    .Connection
                    ?.Node;
            }
            Assert.NotNull(hostOwnedInput);
            Assert.Equal("ImageScale", hostOwnedInput.ClassTypeName);
            failedHostScaleId = hostOwnedInput.Id;
            actualPreHostMediaId =
                hostOwnedInput.FindInput("image").Connection?.Node?.Id;
            Assert.NotNull(actualPreHostMediaId);
            Assert.NotEqual(failedHostScaleId, actualPreHostMediaId);
            actualPreHostMediaDefinition =
                info.Generator.Workflow[actualPreHostMediaId]?.DeepClone();
            Assert.NotNull(actualPreHostMediaDefinition);
            Assert.True(ReachesUpstream(
                bridge,
                failedLatent,
                actualPreHostMediaId));
            throw new InvalidOperationException("unit-test Wan 5B host post failure");
        };
        int originalPostHandlerCount =
            WorkflowGenerator.AltImageToVideoPostHandlers.Count;
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(failAfterHostLatent);
        InvalidOperationException error;
        try
        {
            error = Assert.Throws<InvalidOperationException>(() =>
                WorkflowTestHarness.GenerateWithStepsAndState(
                    input,
                    WanSteps().Append(installPriorState),
                    WanSourceFeatures));
        }
        finally
        {
            Assert.True(
                WorkflowGenerator.AltImageToVideoPostHandlers.Remove(
                    failAfterHostLatent));
        }

        Assert.Equal("unit-test Wan 5B host post failure", error.Message);
        Assert.NotNull(captured);
        Assert.NotNull(failedLatentId);
        Assert.NotNull(failedLatentStartImageId);
        Assert.NotNull(failedHostScaleId);
        Assert.NotNull(actualPreHostMediaId);
        Assert.False(captured.Workflow.ContainsKey(failedLatentId));
        Assert.False(captured.Workflow.ContainsKey(failedLatentStartImageId));
        Assert.False(captured.Workflow.ContainsKey(failedHostScaleId));
        Assert.True(captured.Workflow.ContainsKey(actualPreHostMediaId));
        Assert.True(JToken.DeepEquals(
            actualPreHostMediaDefinition,
            captured.Workflow[actualPreHostMediaId]));
        Assert.True(captured.Workflow.ContainsKey(priorSourceId));
        Assert.True(captured.Workflow.ContainsKey(priorLatentId));
        Assert.True(captured.Workflow.ContainsKey(priorConsumerId));
        Assert.True(captured.Workflow.ContainsKey(priorAudioVaeId));
        Assert.Same(ambientAudioVae, captured.CurrentAudioVae);
        Assert.True(captured.NodeHelpers.ContainsKey(priorCacheKey));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            captured.NodeHelpers.Keys);
        Assert.NotNull(original);
        AssertLoraParamsEqual(original, CaptureLoraParams(input));
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(0),
            captured.UserInput.SectionParamOverrides.Keys);
        Assert.Equal(
            originalPostHandlerCount,
            WorkflowGenerator.AltImageToVideoPostHandlers.Count);
        using WorkflowBridge finalBridge = WorkflowBridge.Create(captured.Workflow);
        ComfyNode priorLatent = finalBridge.Graph.Nodes[priorLatentId];
        Assert.NotEmpty(
            finalBridge.Graph.FindInputsConnectedTo(priorLatent.FindOutput(0)));
        AssertNoDanglingNodeRefs(captured.Workflow);
        AssertAcyclic(finalBridge);
    }

    [Fact]
    public void Wan5b_post_host_cleanup_failure_is_suppressed_only_for_an_existing_host_failure()
    {
        InvalidOperationException cleanupFailure = new("unit-test cleanup failure");
        InvalidOperationException hostFailure = new("unit-test host failure");

        Exception whileHostFailed = Record.Exception(
            () => WanStockHostVideoBehavior.RunPostHostCleanup(
                () => throw cleanupFailure,
                hostFailure));
        InvalidOperationException afterHostSucceeded =
            Assert.Throws<InvalidOperationException>(
                () => WanStockHostVideoBehavior.RunPostHostCleanup(
                    () => throw cleanupFailure,
                    hostConstructionError: null));

        Assert.Null(whileHostFailed);
        Assert.Same(cleanupFailure, afterHostSucceeded);
    }

    [Fact]
    public void Wan5b_native_text_restores_ambient_video_and_audio_scopes_after_model_prep_failure()
    {
        using SwarmUiTestContext context = new(clearModelGenSteps: false);
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 10)));
        const string ambientAudioVaeId = "unit-native-text-ambient-audio-vae";
        WorkflowGenerator captured = null;
        WGNodeData ambientAudioVae = null;
        bool armed = false;
        WorkflowGenerator.WorkflowGenStep installAmbientState = new(g =>
        {
            captured = g;
            g.Workflow[ambientAudioVaeId] = new JObject
            {
                ["class_type"] = "UnitTest_NativeTextAmbientAudioVae",
                ["inputs"] = new JObject(),
            };
            ambientAudioVae = new(
                new JArray(ambientAudioVaeId, 0),
                g,
                WGNodeData.DT_AUDIOVAE,
                null);
            g.CurrentAudioVae = ambientAudioVae;
            g.IsImageToVideo = true;
            Assert.False(g.IsImageToVideoSwap);
            armed = true;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);
        WorkflowGenerator.AddModelGenStep(g =>
        {
            if (armed)
            {
                Assert.True(g.IsImageToVideo);
                Assert.False(g.IsImageToVideoSwap);
                throw new InvalidOperationException(
                    "unit-test native Wan model prep failure");
            }
        }, -9);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())
                    .Append(installAmbientState)));

        Assert.Equal("unit-test native Wan model prep failure", error.Message);
        Assert.NotNull(captured);
        Assert.True(captured.IsImageToVideo);
        Assert.False(captured.IsImageToVideoSwap);
        Assert.Same(ambientAudioVae, captured.CurrentAudioVae);
        Assert.True(captured.Workflow.ContainsKey(ambientAudioVaeId));
    }

    [Fact]
    public void Wan_low_continuation_loader_failure_restores_all_temporary_state()
    {
        using SwarmUiTestContext context = new(clearModelGenSteps: false);
        TestModelBundle models =
            TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Low_Failure_Prompt.safetensors");
        AddLoraModel("UnitTest_Wan_Low_Failure_Persisted.safetensors");
        T2IModel high = AddDistinctWanModel(
            models.VideoModel,
            "Wan2.2-I2V-A14B-HighNoise.safetensors");
        T2IModel low = AddDistinctWanModel(
            models.VideoModel,
            "Wan2.2-I2V-A14B-LowNoise.safetensors");
        JObject lowStage = MakeStage(
            low.Name,
            "PreviousStage",
            control: 0.5,
            steps: 8);
        lowStage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Low_Failure_Persisted",
            ["weight"] = 0.7,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            high,
            JsonSingleClipStages(
                MakeStage(high.Name, "Generated", control: 1, steps: 8),
                lowStage),
            prompt: "global <videoclip[0,0]>high-stage "
                + "<videoclip[0,1]>low-stage "
                + "<lora:UnitTest_Wan_Low_Failure_Prompt:0.8>");
        input.Set(
            T2IParamTypes.Steps,
            31,
            T2IParamInput.SectionID_VideoSwap);
        input.Set(
            T2IParamTypes.CFGScale,
            9,
            T2IParamInput.SectionID_VideoSwap);
        WorkflowGenerator captured = null;
        LoraParamState original = null;
        bool armed = false;
        WorkflowGenerator.WorkflowGenStep snapshot = new(g =>
        {
            captured = g;
            original = CaptureLoraParams(g.UserInput);
            armed = true;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);
        WorkflowGenerator.AddModelGenStep(g =>
        {
            if (armed && g.IsImageToVideoSwap)
            {
                throw new InvalidOperationException(
                    "unit-test Wan low continuation loader failure");
            }
        }, -9);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(snapshot)));

        Assert.Equal(
            "unit-test Wan low continuation loader failure",
            error.Message);
        Assert.NotNull(captured);
        Assert.False(captured.IsImageToVideo);
        Assert.False(captured.IsImageToVideoSwap);
        Assert.NotNull(original);
        AssertLoraParamsEqual(original, CaptureLoraParams(input));
        Assert.Equal(
            31,
            input.GetNullable(
                T2IParamTypes.Steps,
                T2IParamInput.SectionID_VideoSwap,
                false));
        Assert.Equal(
            9,
            input.GetNullable(
                T2IParamTypes.CFGScale,
                T2IParamInput.SectionID_VideoSwap,
                false));
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(0),
            input.SectionParamOverrides.Keys);
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(1),
            input.SectionParamOverrides.Keys);
        Assert.DoesNotContain(
            $"modelloader_{low.Name}_image2video",
            captured.NodeHelpers.Keys);
    }

    [Fact]
    public void Missing_typed_pre_core_handoff_fails_closed()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator.WorkflowGenStep corrupt = new(
            g => g.GetVideoExecutionPlanContext()
                .ExecutePrepared(host => host.DropCoreOutput()),
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        corrupt,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains("captured root state is missing", error.Message);
    }

    [Fact]
    public void Removed_pre_core_media_fails_closed()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        string rootMediaNodeId = null;
        WorkflowGenerator.WorkflowGenStep remember = new(
            g => rootMediaNodeId = $"{g.CurrentMedia.Path[0]}",
            Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1);
        WorkflowGenerator.WorkflowGenStep corrupt = new(g =>
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            bridge.RemoveNode(rootMediaNodeId);
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        remember,
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        corrupt,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains("host root media is missing or no longer resolves", error.Message);
    }

    [Fact]
    public void Capture_refuses_an_unresolvable_root_before_any_stage_loads_its_model()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator captured = null;
        WorkflowGenerator.WorkflowGenStep breakRoot = new(g =>
        {
            captured = g;
            g.CurrentMedia = g.CurrentMedia.WithPath(new JArray("removed-root", 0));
        }, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.01);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([breakRoot, WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains("host root media is missing or no longer resolves", error.Message);
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            captured.NodeHelpers.Keys);
    }

    [Fact]
    public void Root_handoff_restores_an_exactly_absent_VAE()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        bool restoredNull = false;
        WorkflowGenerator.WorkflowGenStep clearRootVae = new(
            g => g.CurrentVae = null,
            Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1);
        WorkflowGenerator.WorkflowGenStep installCoreVae = new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            UnknownNode coreVae = bridge.AddStub("UnitTest_CoreVae", "wan-core-vae")
                .WithOutputs(WGNodeData.DT_VAE);
            g.CurrentVae = coreVae.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);
        WorkflowGenerator.WorkflowGenStep observe = new(
            g => restoredNull = g.CurrentVae is null,
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput + 0.01);

        WorkflowTestHarness.GenerateWithStepsAndState(
            WanInput(models, steps: 10),
            WorkflowTestHarness.Template_BaseOnlyImage()
                .Concat([
                    clearRootVae,
                    WorkflowTestHarness.CoreImageToVideoStep(),
                    installCoreVae,
                    observe,
                ])
                .Concat(WorkflowTestHarness.VideoStagesSteps()));

        Assert.True(restoredNull);
    }

    private static T2IParamInput WanInput(TestModelBundle models, int steps) =>
        BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated", steps: steps)));

    private static JObject MakeWanInitVideoClip(
        string model,
        double control,
        int steps,
        params JObject[] laterStages)
    {
        JObject first = MakeStage(model, "Generated", control: control, steps: steps);
        first.Remove("imageReference");
        JObject clip = MakeClip([first, .. laterStages]);
        clip["duration"] = WanInitVideoDuration;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,"
                + Convert.ToBase64String([0x11, 0x22, 0x33]),
            ["fileName"] = "wan-source.mp4",
            ["startSeconds"] = WanInitVideoStartSeconds,
        };
        return clip;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> WanSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<ComfyNode> NodesOfClass(WorkflowBridge bridge, string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);

    private static IEnumerable<ComfyNode> SamplerNodes(WorkflowBridge bridge) =>
        NodesOfClass(bridge, "SwarmKSampler")
            .Concat(NodesOfClass(bridge, "KSamplerAdvanced"));

    private sealed record LoraParamState(
        IReadOnlyList<string> Loras,
        IReadOnlyList<string> Weights,
        IReadOnlyList<string> TextEncoderWeights,
        IReadOnlyList<string> Confinements);

    private static LoraParamState CaptureLoraParams(T2IParamInput input) =>
        new(
            [.. input.Get(T2IParamTypes.Loras) ?? []],
            [.. input.Get(T2IParamTypes.LoraWeights) ?? []],
            [.. input.Get(T2IParamTypes.LoraTencWeights) ?? []],
            [.. input.Get(T2IParamTypes.LoraSectionConfinement) ?? []]);

    private static void AssertLoraParamsEqual(
        LoraParamState expected,
        LoraParamState actual)
    {
        Assert.Equal(expected.Loras, actual.Loras);
        Assert.Equal(expected.Weights, actual.Weights);
        Assert.Equal(expected.TextEncoderWeights, actual.TextEncoderWeights);
        Assert.Equal(expected.Confinements, actual.Confinements);
    }

    private static T2IModel AddLoraModel(string name)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            handler = new() { ModelType = "LoRA" };
            Program.T2IModelSets["LoRA"] = handler;
        }
        T2IModel model = TestStubModel.Create(handler, name);
        handler.Models[model.Name] = model;
        return model;
    }

    private static T2IModel AddDistinctWanModel(T2IModel recognizedModel, string name)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, name), name)
        {
            ModelClass = recognizedModel.ModelClass,
        };
        handler.Models[model.Name] = model;
        return model;
    }
}
