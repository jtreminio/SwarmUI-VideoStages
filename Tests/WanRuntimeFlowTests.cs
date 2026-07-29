using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// The Wan slice end to end: a real generated workflow, built through SwarmUI's own
/// image-to-video construction, with the host's core video pass handed off to the clip.
/// </summary>
[Collection("VideoStagesTests")]
public class WanRuntimeFlowTests
{
    private const int WanSourcedFrames = 17;
    private const double WanSourcedDuration = 0.6;
    private const double WanSourcedStartSeconds = 1;
    private static readonly string[] WanSourceFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    private sealed class PreflightSnapshot
    {
        internal WorkflowGenerator Generator { get; private set; }
        internal JObject Workflow { get; private set; }
        internal WGNodeData Media { get; private set; }
        internal Dictionary<string, string> NodeHelpers { get; private set; }

        internal WorkflowGenerator.WorkflowGenStep Step() => new(g =>
        {
            Generator = g;
            Workflow = (JObject)g.Workflow.DeepClone();
            Media = g.CurrentMedia;
            NodeHelpers = new(g.NodeHelpers);
        }, Constants.WorkflowStepPriority.PreflightRequest - 0.5);

        internal void AssertUnchanged()
        {
            Assert.True(JToken.DeepEquals(Workflow, Generator.Workflow));
            Assert.Same(Media, Generator.CurrentMedia);
            Assert.Equal(NodeHelpers, Generator.NodeHelpers);
        }
    }

    [Fact]
    public void Wan_clip_generates_from_the_host_image_and_replaces_the_core_video()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        string loaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        string hostLoaderTuple = null;
        bool hostLoaderTupleWasInvalidated = false;
        WorkflowGenerator.WorkflowGenStep captureHostLoaderTuple = new(
            g => g.NodeHelpers.TryGetValue(loaderKey, out hostLoaderTuple),
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);
        WorkflowGenerator.WorkflowGenStep observeHandoffCleanup = new(
            g => hostLoaderTupleWasInvalidated = !g.NodeHelpers.ContainsKey(loaderKey),
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput + 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostLoaderTuple,
                        observeHandoffCleanup,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The host core pass populated its six-part loader tuple. The root handoff pruned those
        // nodes and invalidated the tuple before Wan asked the same host builder for its clip
        // graph, so no Wan-local delete is needed to avoid reusing dangling references.
        Assert.NotNull(hostLoaderTuple);
        Assert.Equal(6, hostLoaderTuple.Split(':').Length);
        Assert.True(hostLoaderTupleWasInvalidated);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);

        // One Wan conditioning node, not two: the host's own core pass built one first and the
        // handoff pruned it, so only the clip's generation survives.
        ComfyNode imageToVideo = Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode sampler = Assert.Single(bridge.Graph.FindDownstream(imageToVideo.FindOutput(2)));
        Assert.Equal(10, sampler.FindInput("steps").LiteralAsInt());

        // The clip generates at the timeline resolution, from the still the host entered with
        // rather than the video the host made out of it.
        Assert.Single(
            NodesOfClass(bridge, "ImageScale"),
            node => node.FindInput("width").LiteralAsInt() == 512
                && node.FindInput("height").LiteralAsInt() == 512);

        // Wan owns no runtime key past its own host phases.
        Assert.DoesNotContain(
            generator.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
    }

    [Fact]
    public void Wan5b_generated_entry_uses_its_native_latent_profile_and_decoded_contract()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();

        (JObject workflow, WorkflowGenerator generator) =
            GenerateWanClip(models, steps: 10);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode latent = Assert.Single(
            NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Same(latent, sampler.FindInput("latent_image").Connection?.Node);
        Assert.Equal(512, latent.FindInput("width").LiteralAsInt());
        Assert.Equal(512, latent.FindInput("height").LiteralAsInt());
        Assert.Equal(13, latent.FindInput("length").LiteralAsInt());
        Assert.Equal(10, sampler.FindInput("steps").LiteralAsInt());
        Assert.Equal(0, sampler.FindInput("start_at_step").LiteralAsInt());
        Assert.True(ReachesUpstream(
            bridge,
            latent.FindInput("start_image").Connection?.Node,
            Assert.Single(
                NodesOfClass(bridge, "UnitTest_Model")).Id));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        string loaderTuple =
            generator.NodeHelpers[$"modelloader_{models.VideoModel.Name}_image2video"];
        AssertLoaderTupleIsLive(workflow, loaderTuple);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan5b_sourced_multistage_partial_and_passthrough_preserve_decoded_provenance()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        JObject partial = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            steps: 12);
        JObject passthrough = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0,
            steps: 13);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 1,
                steps: 10,
                partial,
                passthrough)).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode source = AssertWanSourceConformChain(
            bridge,
            width: 512,
            height: 512);
        ComfyNode first = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode second = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 12);
        Assert.DoesNotContain(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 13);
        ComfyNode firstLatent =
            Assert.IsAssignableFrom<ComfyNode>(
                first.FindInput("latent_image").Connection?.Node);
        Assert.Equal("Wan22ImageToVideoLatent", firstLatent.ClassTypeName);
        Assert.True(ReachesUpstream(
            bridge,
            firstLatent.FindInput("start_image").Connection?.Node,
            source.Id));
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(12, 0.5),
            second.FindInput("start_at_step").LiteralAsInt());
        VAEEncodeNode secondInput = Assert.IsType<VAEEncodeNode>(
            second.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, secondInput, first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath(generator.CurrentMedia.Path)?.Node,
            second.Id));
        Assert.Equal(WanSourcedFrames, generator.CurrentMedia.Frames);
        ComfyNode retainedNativeLatent = Assert.Single(
            NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
        Assert.Same(firstLatent, retainedNativeLatent);
        Assert.NotEmpty(
            bridge.Graph.FindInputsConnectedTo(retainedNativeLatent.FindOutput(0)));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan5b_partial_host_failure_prunes_only_new_unused_latent_and_restores_scopes()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        AddLoraModel("UnitTest_Wan5b_Failure_Prompt.safetensors");
        AddLoraModel("UnitTest_Wan5b_Failure_Persisted.safetensors");
        JObject clip = MakeWanSourcedClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 12);
        JObject stage = (JObject)((JArray)clip["stages"])[0];
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
            () => WanGenerationSession.RunPostHostCleanup(
                () => throw cleanupFailure,
                hostFailure));
        InvalidOperationException afterHostSucceeded =
            Assert.Throws<InvalidOperationException>(
                () => WanGenerationSession.RunPostHostCleanup(
                    () => throw cleanupFailure,
                    hostConstructionError: null));

        Assert.Null(whileHostFailed);
        Assert.Same(cleanupFailure, afterHostSucceeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wan_persisted_and_prompt_LoRAs_use_the_generic_model_only_loader(
        bool useFiveBProfile)
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = useFiveBProfile
            ? TestModelFactory.CreateBaseAndWan22Ti2v5bModels()
            : TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan5b_Prompt.safetensors");
        AddLoraModel("UnitTest_Wan5b_Persisted.safetensors");
        AddLoraModel("UnitTest_Wan5b_Prompt_ModelZero.safetensors");
        AddLoraModel("UnitTest_Wan5b_Persisted_ModelZero.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["loras"] = new JArray(
            new JObject
            {
                ["name"] = "UnitTest_Wan5b_Persisted",
                ["weight"] = 0.6,
                ["textEncoderWeight"] = 0.7,
            },
            new JObject
            {
                ["name"] = "UnitTest_Wan5b_Persisted_ModelZero",
                ["weight"] = 0,
                ["textEncoderWeight"] = 0.9,
            });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage),
            prompt:
                "global <videoclip[0,0]><lora:UnitTest_Wan5b_Prompt:0.4:0.8>"
                + "<lora:UnitTest_Wan5b_Prompt_ModelZero:0:0.9>");
        LoraParamState original = null;
        WorkflowGenerator.WorkflowGenStep snapshot = new(
            g => original = CaptureLoraParams(g.UserInput),
            Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(snapshot));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode prompt = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan5b_Prompt.safetensors");
        ComfyNode persisted = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan5b_Persisted.safetensors");
        Assert.Equal("LoraLoaderModelOnly", prompt.ClassTypeName);
        Assert.Equal("LoraLoaderModelOnly", persisted.ClassTypeName);
        Assert.DoesNotContain(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                is "UnitTest_Wan5b_Prompt_ModelZero.safetensors"
                    or "UnitTest_Wan5b_Persisted_ModelZero.safetensors");
        Assert.Equal(prompt.Id, persisted.FindInput("model").Connection?.Node?.Id);
        Assert.True(ModelBranchReaches(
            bridge,
            Assert.Single(SamplerNodes(bridge)),
            persisted));
        Assert.NotNull(original);
        AssertLoraParamsEqual(original, CaptureLoraParams(input));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Generated_Wan_stage_applies_its_persisted_LoRA_scope()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Generated_Lora.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Generated_Lora",
            ["weight"] = 0.45,
            ["textEncoderWeight"] = 0.2,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        ComfyNode lora = Assert.Single(LoraLoaderNodesOf(bridge));
        Assert.Equal(
            "UnitTest_Wan_Generated_Lora.safetensors",
            lora.FindInput("lora_name").LiteralAsString());
        Assert.Equal("LoraLoaderModelOnly", lora.ClassTypeName);
        Assert.Equal(0.45, lora.FindInput("strength_model").LiteralAsDouble().Value, 6);
        Assert.True(ReachesUpstream(
            bridge,
            sampler.FindInput("model").Connection?.Node,
            lora.Id));
        Assert.False(input.TryGet(T2IParamTypes.Loras, out List<string> _));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Failed_Wan_LoRA_load_restores_params_and_evicts_transient_cache()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Unrelated_Lora.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Missing_Lora",
            ["weight"] = 0.45,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage));
        WorkflowGenerator captured = null;
        WorkflowGenerator.WorkflowGenStep capture = new(
            g => captured = g,
            Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(capture)));

        Assert.Contains("UnitTest_Wan_Missing_Lora", error.Message);
        Assert.NotNull(captured);
        Assert.False(input.TryGet(T2IParamTypes.Loras, out List<string> _));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            captured.NodeHelpers.Keys);
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(0),
            captured.UserInput.SectionParamOverrides.Keys);
    }

    [Fact]
    public void Failed_Wan_prompt_LoRA_restores_nested_params_cache_and_stage_section()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Prompt_Before_Failure.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Missing_After_Prompt",
            ["weight"] = 0.45,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage),
            prompt:
                "global <videoclip[0,0]><lora:UnitTest_Wan_Prompt_Before_Failure:0.4>");
        WorkflowGenerator captured = null;
        LoraParamState original = null;
        WorkflowGenerator.WorkflowGenStep capture = new(
            g =>
            {
                captured = g;
                original = CaptureLoraParams(g.UserInput);
            },
            Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(capture)));

        Assert.Contains("UnitTest_Wan_Missing_After_Prompt", error.Message);
        Assert.NotNull(captured);
        Assert.NotNull(original);
        AssertLoraParamsEqual(original, CaptureLoraParams(input));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            captured.NodeHelpers.Keys);
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(0),
            captured.UserInput.SectionParamOverrides.Keys);
        using WorkflowBridge bridge = WorkflowBridge.Create(captured.Workflow);
        AssertNoDanglingNodeRefs(captured.Workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Sourced_generating_Wan_stage_applies_its_clip_LoRA()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Source_Lora.safetensors");
        JObject clip = MakeWanSourcedClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 10);
        clip["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Source_Lora",
            ["weight"] = 0.6,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                features: WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        _ = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        ComfyNode lora = Assert.Single(LoraLoaderNodesOf(bridge));
        Assert.True(ReachesUpstream(
            bridge,
            sampler.FindInput("model").Connection?.Node,
            lora.Id));
        Assert.Equal(0.6, lora.FindInput("strength_model").LiteralAsDouble().Value, 6);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_stage_LoRA_scopes_and_high_loader_cache_do_not_leak()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Scoped_Lora.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            control: 1,
            steps: 10);
        first["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Scoped_Lora",
            ["weight"] = 0.25,
        });
        JObject second = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            steps: 11);
        second["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Scoped_Lora",
            ["weight"] = 0.75,
        });
        JObject third = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            steps: 12);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second, third));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(3, samplers.Length);
        ComfyNode firstSampler = SamplerBySeed(samplers, 43);
        ComfyNode secondSampler = SamplerBySeed(samplers, 44);
        ComfyNode thirdSampler = SamplerBySeed(samplers, 45);
        ComfyNode firstLora = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("strength_model").LiteralAsDouble() == 0.25);
        ComfyNode secondLora = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("strength_model").LiteralAsDouble() == 0.75);
        Assert.True(ModelBranchReaches(bridge, firstSampler, firstLora));
        Assert.False(ModelBranchReaches(bridge, firstSampler, secondLora));
        Assert.True(ModelBranchReaches(bridge, secondSampler, secondLora));
        Assert.False(ModelBranchReaches(bridge, secondSampler, firstLora));
        Assert.False(ModelBranchReaches(bridge, thirdSampler, firstLora));
        Assert.False(ModelBranchReaches(bridge, thirdSampler, secondLora));
        string liveTuple =
            generator.NodeHelpers[$"modelloader_{models.VideoModel.Name}_image2video"];
        AssertLoaderTupleIsLive(workflow, liveTuple);
        Assert.Equal(
            thirdSampler.FindInput("model").Connection?.Node?.Id,
            liveTuple.Split(':')[0]);
        Assert.False(input.TryGet(T2IParamTypes.Loras, out List<string> _));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_prompt_LoRA_clip_stage_and_bare_confinements_select_exact_generating_passes()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Prompt_Bare.safetensors");
        AddLoraModel("UnitTest_Wan_Prompt_Stage.safetensors");
        AddLoraModel("UnitTest_Wan_Prompt_Clip.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 10);
        JObject second = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            steps: 11);
        JObject third = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 12);
        string prompt =
            "global <videoclip><lora:UnitTest_Wan_Prompt_Bare:0.2>"
            + " <videoclip[0,1]><lora:UnitTest_Wan_Prompt_Stage:0.3>"
            + " <videoclip[1]><lora:UnitTest_Wan_Prompt_Clip:0.4>";
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(first, second), MakeClip(third)).ToString(),
            prompt: prompt);

        (JObject workflow, _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode secondSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 11);
        ComfyNode thirdSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 12);
        ComfyNode[] bare =
        [
            .. LoraLoaderNodesOf(bridge).Where(
                node => node.FindInput("lora_name").LiteralAsString()
                    == "UnitTest_Wan_Prompt_Bare.safetensors"),
        ];
        Assert.NotEmpty(bare);
        Assert.Single(bare, node => ModelBranchReaches(bridge, firstSampler, node));
        Assert.Single(bare, node => ModelBranchReaches(bridge, secondSampler, node));
        Assert.Single(bare, node => ModelBranchReaches(bridge, thirdSampler, node));
        Assert.All(
            bare,
            node => Assert.Contains(
                new[] { firstSampler, secondSampler, thirdSampler },
                sampler => ModelBranchReaches(bridge, sampler, node)));

        ComfyNode stageScoped = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Prompt_Stage.safetensors");
        Assert.False(ModelBranchReaches(bridge, firstSampler, stageScoped));
        Assert.True(ModelBranchReaches(bridge, secondSampler, stageScoped));
        Assert.False(ModelBranchReaches(bridge, thirdSampler, stageScoped));

        ComfyNode clipScoped = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Prompt_Clip.safetensors");
        Assert.False(ModelBranchReaches(bridge, firstSampler, clipScoped));
        Assert.False(ModelBranchReaches(bridge, secondSampler, clipScoped));
        Assert.True(ModelBranchReaches(bridge, thirdSampler, clipScoped));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_prompt_LoRA_passthrough_scope_is_inert_and_later_unscoped_loader_is_durable()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Prompt_Generating.safetensors");
        AddLoraModel("UnitTest_Wan_Prompt_Passthrough.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 10);
        JObject passthrough = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0,
            steps: 11);
        JObject third = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            steps: 12);
        string prompt =
            "global <videoclip[0,0]><lora:UnitTest_Wan_Prompt_Generating:0.3>"
            + " <videoclip[0,1]><lora:UnitTest_Wan_Prompt_Passthrough:0.4>";
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, passthrough, third),
            prompt: prompt);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode thirdSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 12);
        ComfyNode generating = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Prompt_Generating.safetensors");
        Assert.True(ModelBranchReaches(bridge, firstSampler, generating));
        Assert.False(ModelBranchReaches(bridge, thirdSampler, generating));
        Assert.DoesNotContain(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Prompt_Passthrough.safetensors");
        string liveTuple =
            generator.NodeHelpers[$"modelloader_{models.VideoModel.Name}_image2video"];
        AssertLoaderTupleIsLive(workflow, liveTuple);
        Assert.Equal(
            thirdSampler.FindInput("model").Connection?.Node?.Id,
            liveTuple.Split(':')[0]);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_prompt_and_persisted_LoRAs_compose_in_order_and_restore_host_lists()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Prompt_Composed.safetensors");
        AddLoraModel("UnitTest_Wan_Persisted_Composed.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_Persisted_Composed",
            ["weight"] = 0.6,
            ["textEncoderWeight"] = 0.5,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage),
            prompt:
                "global <videoclip[0,0]><lora:UnitTest_Wan_Prompt_Composed:0.35>");
        LoraParamState original = null;
        WorkflowGenerator.WorkflowGenStep snapshot = new(
            g => original = CaptureLoraParams(g.UserInput),
            Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(snapshot));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode promptLora = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Prompt_Composed.safetensors");
        ComfyNode persistedLora = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Persisted_Composed.safetensors");
        Assert.Equal(
            promptLora.Id,
            persistedLora.FindInput("model").Connection?.Node?.Id);
        Assert.True(ModelBranchReaches(
            bridge,
            Assert.Single(SamplerNodes(bridge)),
            persistedLora));
        Assert.NotNull(original);
        AssertLoraParamsEqual(original, CaptureLoraParams(input));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Two_Wan_stages_use_previous_decoded_video_and_exact_control_start_step()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel secondModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Second.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            control: 1,
            steps: 10,
            cfgScale: 4);
        first.Remove("imageReference");
        JObject second = MakeStage(
            secondModel.Name,
            "PreviousStage",
            control: 0.35,
            steps: 12,
            cfgScale: 6.5,
            sampler: "dpmpp_2m",
            scheduler: "karras");
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second),
            prompt: "global <videoclip[0,0]>first-stage-prompt "
                + "<videoclip[0,1]>second-stage-prompt");
        VideoStagesSpec parsed = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(2, samplers.Length);
        ComfyNode firstSampler = Assert.Single(
            samplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == 43);
        ComfyNode secondSampler = Assert.Single(
            samplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == 44);
        AssertSamplerSettings(firstSampler, 10, 4, "euler", "normal");
        AssertSamplerSettings(secondSampler, 12, 6.5, "dpmpp_2m", "karras");
        Assert.Equal(0, firstSampler.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(12, 0.35),
            secondSampler.FindInput("start_at_step").LiteralAsInt());
        AssertSamplerModelSource(bridge, firstSampler, models.VideoModel.Name);
        AssertSamplerModelSource(bridge, secondSampler, secondModel.Name);

        VAEEncodeNode previousVideoEncode = Assert.IsType<VAEEncodeNode>(
            secondSampler.FindInput("latent_image").Connection?.Node);
        VAEDecodeNode previousStageDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => ReachesUpstream(bridge, previousVideoEncode, decode.Id)
                && ReachesUpstream(bridge, decode, firstSampler.Id));
        ComfyNode secondConditioning = secondSampler.FindInput("positive").Connection?.Node;
        Assert.Equal("WanImageToVideo", secondConditioning?.ClassTypeName);
        Assert.True(ReachesUpstream(
            bridge,
            secondConditioning.FindInput("start_image").Connection!.Node,
            previousStageDecode.Id));
        Assert.True(ReachesUpstream(bridge, previousStageDecode, firstSampler.Id));
        ComfyNode firstConditioning = firstSampler.FindInput("positive").Connection?.Node;
        Assert.Equal("WanImageToVideo", firstConditioning?.ClassTypeName);
        Assert.Contains(
            "first-stage-prompt",
            NodeText(firstConditioning.FindInput("positive").Connection?.Node));
        Assert.Contains(
            "second-stage-prompt",
            NodeText(secondConditioning.FindInput("positive").Connection?.Node));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Partial_sourced_Wan_stage_uses_conformed_video_for_conditioning_and_latent()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeWanSourcedClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 10);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(513, 509, clip).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        (int expectedWidth, int expectedHeight) = DimensionSnap.Snap(513, 509);
        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(
            bridge,
            expectedWidth,
            expectedHeight);
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(10, 0.5),
            sampler.FindInput("start_at_step").LiteralAsInt());
        VAEEncodeNode sourceEncode = Assert.IsType<VAEEncodeNode>(
            sampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, sourceEncode, sourceWindow.Id));
        ComfyNode conditioning = sampler.FindInput("positive").Connection?.Node;
        Assert.Equal("WanImageToVideo", conditioning?.ClassTypeName);
        ImageFromBatchNode firstFrame = Assert.IsType<ImageFromBatchNode>(
            conditioning.FindInput("start_image").Connection?.Node);
        Assert.Equal(0, firstFrame.BatchIndex.LiteralAsInt());
        Assert.Equal(1, firstFrame.Length.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, firstFrame, sourceWindow.Id));
        ImageFromBatchNode encodedFrames = Assert.IsType<ImageFromBatchNode>(
            sourceEncode.FindInput("pixels").Connection?.Node);
        Assert.Equal(0, encodedFrames.BatchIndex.LiteralAsInt());
        Assert.Equal(WanSourcedFrames, encodedFrames.Length.LiteralAsInt());
        Assert.NotSame(firstFrame, encodedFrames);
        Assert.False(ReachesUpstream(bridge, encodedFrames, firstFrame.Id));
        Assert.False(ReachesUpstream(bridge, sourceEncode, firstFrame.Id));

        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(expectedWidth, generator.CurrentMedia.Width);
        Assert.Equal(expectedHeight, generator.CurrentMedia.Height);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Null(save.Audio.Connection);
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, sampler.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Sourced_Wan_without_optional_filename_materializes_and_runs()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeWanSourcedClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 10);
        ((JObject)clip["sourceVideo"]).Remove("fileName");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.True(ReachesUpstream(bridge, sampler, sourceWindow.Id));
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Full_control_sourced_Wan_stage_uses_only_the_source_first_frame()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 1,
                steps: 10)).ToString());
        WorkflowGenerator.WorkflowGenStep clearHostRoot = new(g =>
        {
            g.CurrentMedia = null;
            g.CurrentVae = null;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([clearHostRoot])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Equal(0, sampler.FindInput("start_at_step").LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        ComfyNode conditioning = sampler.FindInput("positive").Connection?.Node;
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.FindInput("start_image").Connection?.Node,
            sourceWindow.Id));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.DoesNotContain(
            generator.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Sourced_Wan_clip_prunes_the_real_host_core_lineage_and_publishes_only_source_result()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 0.5,
                steps: 10)).ToString());
        string hostSamplerId = null;
        string hostSaveId = null;
        JObject hostSaveDefinition = null;
        string hostPublishedMediaId = null;
        WorkflowGenerator.WorkflowGenStep captureHostCoreLineage = new(g =>
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            hostSamplerId = Assert.Single(SamplerNodes(bridge)).Id;
            SwarmSaveAnimationWSNode hostSave = Assert.Single(
                bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
            hostSaveId = hostSave.Id;
            hostSaveDefinition = (JObject)g.Workflow[hostSaveId].DeepClone();
            Assert.True(ReachesUpstream(
                bridge,
                hostSave.Images.Connection?.Node,
                hostSamplerId));
            hostPublishedMediaId = bridge.ResolvePath(g.CurrentMedia.Path)?.Node.Id;
            Assert.NotNull(hostPublishedMediaId);
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostCoreLineage,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(hostSamplerId);
        Assert.NotNull(hostSaveId);
        Assert.NotNull(hostSaveDefinition);
        Assert.NotNull(hostPublishedMediaId);
        Assert.Null(workflow[hostSamplerId]);
        Assert.Null(workflow[hostPublishedMediaId]);
        Assert.False(JToken.DeepEquals(hostSaveDefinition, workflow[hostSaveId]));

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode wanSampler = Assert.Single(SamplerNodes(bridge));
        Assert.NotEqual(hostSamplerId, wanSampler.Id);
        VAEEncodeNode sourceEncode = Assert.IsType<VAEEncodeNode>(
            wanSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, sourceEncode, sourceWindow.Id));

        INodeOutput published = bridge.ResolvePath(generator.CurrentMedia.Path);
        Assert.NotNull(published);
        Assert.True(ReachesUpstream(bridge, published.Node, wanSampler.Id));
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(hostSaveId, save.Id);
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, wanSampler.Id));
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Null(save.Audio.Connection);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Sourced_Wan_stage_zero_passthrough_publishes_trimmed_source_without_a_sampler()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 0,
                steps: 10)).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        Assert.Empty(SamplerNodes(bridge));
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.True(ReachesUpstream(bridge, trim, sourceWindow.Id));
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Same(trim, save.Images.Connection?.Node);
        Assert.Null(save.Audio.Connection);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Sourced_Wan_passthrough_then_refine_consumes_source_and_publishes_intermediate()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 0,
                steps: 8,
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    steps: 10))).ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Equal(44, sampler.FindInput("noise_seed").LiteralAsLong());
        VAEEncodeNode sourceEncode = Assert.IsType<VAEEncodeNode>(
            sampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, sourceEncode, sourceWindow.Id));
        Assert.Single(NodesOfClass(bridge, "CheckpointLoaderSimple"));
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());

        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        Assert.Equal(2, saves.Length);
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, sourceWindow.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, sampler.Id));
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, sampler.Id));
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath(generator.CurrentMedia.Path).Node,
            sampler.Id));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Later_Wan_passthrough_preserves_previous_stage_for_a_following_refine()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0, steps: 9),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));
        input.Set(T2IParamTypes.OutputIntermediateImages, true);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(2, samplers.Length);
        ComfyNode first = SamplerBySeed(samplers, 43);
        ComfyNode third = SamplerBySeed(samplers, 45);
        Assert.DoesNotContain(
            samplers,
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 44);
        VAEEncodeNode thirdInput = Assert.IsType<VAEEncodeNode>(
            third.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, thirdInput, first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath(generator.CurrentMedia.Path).Node,
            third.Id));

        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        Assert.Equal(3, saves.Length);
        Assert.Equal(
            2,
            saves.Count(save =>
                ReachesUpstream(bridge, save.Images.Connection?.Node, first.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, third.Id)));
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, third.Id));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Sourced_Wan_multistage_swap_chains_from_source_then_prior_low_pass()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Source_Low.safetensors");
        JObject clip = MakeWanSourcedClip(
            models.VideoModel.Name,
            0.8,
            10,
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.8,
                steps: 12));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(4, samplers.Length);
        ComfyNode firstHigh = Assert.Single(
            samplers,
            node => IsHighNoiseSampler(node)
                && node.FindInput("noise_seed").LiteralAsLong() == 43);
        ComfyNode secondHigh = Assert.Single(
            samplers,
            node => IsHighNoiseSampler(node)
                && node.FindInput("noise_seed").LiteralAsLong() == 44);
        ComfyNode firstLow = AssertLowNoiseForHigh(samplers, firstHigh);
        ComfyNode secondLow = AssertLowNoiseForHigh(samplers, secondHigh);
        VAEEncodeNode firstInput = Assert.IsType<VAEEncodeNode>(
            firstHigh.FindInput("latent_image").Connection?.Node);
        VAEEncodeNode secondInput = Assert.IsType<VAEEncodeNode>(
            secondHigh.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, firstInput, sourceWindow.Id));
        Assert.False(ReachesUpstream(bridge, firstInput, firstLow.Id));
        Assert.True(ReachesUpstream(bridge, secondInput, firstLow.Id));
        Assert.False(ReachesUpstream(bridge, secondInput, secondLow.Id));
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath(generator.CurrentMedia.Path).Node,
            secondLow.Id));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_and_sourced_Wan_clips_keep_root_and_source_provenance_isolated(
        bool sourcedFirst)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject generated = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 8));
        generated["duration"] = WanSourcedDuration;
        JObject sourced = MakeWanSourcedClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 10);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(sourcedFirst
                ? [sourced, generated]
                : [generated, sourced]).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode generatedSampler = SamplerBySeed(samplers, sourcedFirst ? 44 : 43);
        ComfyNode sourcedSampler = SamplerBySeed(samplers, sourcedFirst ? 43 : 44);
        ComfyNode generatedConditioning =
            generatedSampler.FindInput("positive").Connection?.Node;
        ComfyNode sourcedConditioning =
            sourcedSampler.FindInput("positive").Connection?.Node;
        Assert.False(ReachesUpstream(
            bridge,
            generatedConditioning.FindInput("start_image").Connection?.Node,
            sourceWindow.Id));
        Assert.True(ReachesUpstream(
            bridge,
            sourcedConditioning.FindInput("start_image").Connection?.Node,
            sourceWindow.Id));
        VAEEncodeNode sourcedInput = Assert.IsType<VAEEncodeNode>(
            sourcedSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, sourcedInput, sourceWindow.Id));
        Assert.False(ReachesUpstream(bridge, sourcedInput, generatedSampler.Id));
        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Equal(new JArray(merged.Id, 0), generator.CurrentMedia.Path);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Later_full_generation_uses_previous_first_frame_without_a_dead_source_encode()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 8),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 1,
                    steps: 10)));

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode first = SamplerBySeed(samplers, 43);
        ComfyNode second = SamplerBySeed(samplers, 44);
        Assert.Equal(0, second.FindInput("start_at_step").LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        ComfyNode secondConditioning = Assert.IsType<WanImageToVideoNode>(
            second.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge,
            secondConditioning.FindInput("start_image").Connection!.Node,
            first.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Json_partial_control_at_one_step_boundary_encodes_the_previous_decoded_video()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(models.VideoModel.Name, steps: 8);
        JObject second = MakeStage(models.VideoModel.Name, control: 0.87, steps: 8);
        first.Remove("imageReference");
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second));

        VideoStagesSpec parsed = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);
        Assert.Equal(1, WanStageSchedulePolicy.StartStep(
            parsed.Clips[0].Stages[1].Steps,
            parsed.Clips[0].Stages[1].Control));

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode firstSampler = SamplerBySeed(samplers, 43);
        ComfyNode secondSampler = SamplerBySeed(samplers, 44);
        Assert.Equal(1, secondSampler.FindInput("start_at_step").LiteralAsInt());
        VAEEncodeNode encodedPrevious = Assert.IsType<VAEEncodeNode>(
            secondSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, encodedPrevious, firstSampler.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Three_Wan_stages_publish_intermediates_and_trim_only_the_final_output()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.25, steps: 12)));
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(3, samplers.Length);
        ComfyNode first = SamplerBySeed(samplers, 43);
        ComfyNode second = SamplerBySeed(samplers, 44);
        ComfyNode third = SamplerBySeed(samplers, 45);
        Assert.Equal(2, bridge.Graph.NodesOfType<VAEEncodeNode>().Count());
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(10, 0.5),
            second.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(12, 0.25),
            third.FindInput("start_at_step").LiteralAsInt());
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(
                second.FindInput("latent_image").Connection?.Node),
            first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(
                third.FindInput("latent_image").Connection?.Node),
            second.Id));

        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        Assert.Equal(3, saves.Length);
        Assert.Single(saves, save => ReferenceEquals(trim, save.Images.Connection?.Node));
        Assert.Equal(
            2,
            saves.Count(save => save.Images.Connection?.Node is VAEDecodeNode));
        Assert.All(saves, save => Assert.Equal(24.0, save.Fps.LiteralAsDouble()));
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(9, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_swap_stage_chain_feeds_each_next_stage_from_the_prior_low_pass()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 11),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.8,
                    steps: 17)));
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(4, samplers.Length);
        ComfyNode firstHigh = Assert.Single(
            samplers,
            node => IsHighNoiseSampler(node)
                && node.FindInput("steps").LiteralAsInt() == 11);
        ComfyNode secondHigh = Assert.Single(
            samplers,
            node => IsHighNoiseSampler(node)
                && node.FindInput("steps").LiteralAsInt() == 17);
        ComfyNode firstLow = AssertLowNoiseForHigh(samplers, firstHigh);
        ComfyNode secondLow = AssertLowNoiseForHigh(samplers, secondHigh);
        int secondStart = WanStageSchedulePolicy.StartStep(17, 0.8);
        int secondHighEnd = WanStageSchedulePolicy.HostHighEndStep(17, 0.6);
        Assert.True(secondStart < secondHighEnd);
        Assert.Equal(secondStart, secondHigh.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(secondHighEnd, secondHigh.FindInput("end_at_step").LiteralAsInt());
        AssertSamplerSettings(firstLow, 14, 8.25, "dpmpp_2m", "karras");
        AssertSamplerSettings(secondLow, 14, 8.25, "dpmpp_2m", "karras");
        VAEEncodeNode secondInput = Assert.IsType<VAEEncodeNode>(
            secondHigh.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, secondInput, firstLow.Id));
        Assert.False(ReachesUpstream(bridge, secondInput, secondLow.Id));
        ComfyNode secondConditioning = secondHigh.FindInput("positive").Connection?.Node;
        Assert.Equal("WanImageToVideo", secondConditioning?.ClassTypeName);
        Assert.True(ReachesUpstream(
            bridge,
            secondConditioning.FindInput("start_image").Connection!.Node,
            firstLow.Id));
        INodeOutput finalOutput = bridge.ResolvePath(generator.CurrentMedia.Path);
        Assert.NotNull(finalOutput);
        Assert.True(ReachesUpstream(bridge, finalOutput.Node, secondLow.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Single_Wan_clip_uses_the_global_end_image_and_prunes_the_host_Flf_pass()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));
        string discardedHostFlfId = null;
        WorkflowGenerator.WorkflowGenStep captureHostFlf = new(g =>
        {
            discardedHostFlfId = Assert.Single(
                ((JObject)g.Workflow).Properties(),
                property => property.Value["class_type"]?.ToString()
                    == "WanFirstLastFrameToVideo").Name;
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostFlf,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(discardedHostFlfId);
        Assert.Null(workflow[discardedHostFlfId]);
        ComfyNode flf = Assert.Single(NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode endScale = flf.FindInput("end_image").Connection?.Node;
        Assert.NotNull(endScale);
        Assert.Equal("ImageScale", endScale.ClassTypeName);
        Assert.Equal(512, endScale.FindInput("width").LiteralAsInt());
        Assert.Equal(512, endScale.FindInput("height").LiteralAsInt());
        Assert.Equal("lanczos", endScale.FindInput("upscale_method").LiteralAsString());
        LoadImageNode endLoad = Assert.IsType<LoadImageNode>(
            endScale.FindInput("image").Connection?.Node);
        Assert.Equal("${videoendframe}", endLoad.Image.LiteralAsString());

        ComfyNode startImage = flf.FindInput("start_image").Connection?.Node;
        Assert.NotNull(startImage);
        Assert.NotSame(endScale, startImage);
        Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => ReachesUpstream(bridge, startImage, decode.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
    }

    [Fact]
    public void Wan_swap_composes_with_end_frame_and_global_low_noise_overrides()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = WanInput(models, steps: 11);
        input.Set(T2IParamTypes.VideoSwapModel, lowNoiseModel);
        input.Set(T2IParamTypes.VideoSwapPercent, 0.6);
        input.Set(T2IParamTypes.Steps, 14, T2IParamInput.SectionID_VideoSwap);
        input.Set(T2IParamTypes.CFGScale, 8.25, T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SamplerParam,
            "dpmpp_2m",
            T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SchedulerParam,
            "karras",
            T2IParamInput.SectionID_VideoSwap);
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x02], MediaType.ImagePng));
        string highLoaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        string lowLoaderKey = $"modelloader_{lowNoiseModel.Name}_image2video";
        string discardedHighTuple = null;
        string discardedLowTuple = null;
        bool discardedTuplesWereInvalidated = false;
        WorkflowGenerator.WorkflowGenStep captureDiscardedTuples = new(g =>
        {
            g.NodeHelpers.TryGetValue(highLoaderKey, out discardedHighTuple);
            g.NodeHelpers.TryGetValue(lowLoaderKey, out discardedLowTuple);
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);
        WorkflowGenerator.WorkflowGenStep observeHandoffCleanup = new(
            g => discardedTuplesWereInvalidated =
                !g.NodeHelpers.ContainsKey(highLoaderKey)
                && !g.NodeHelpers.ContainsKey(lowLoaderKey),
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput + 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureDiscardedTuples,
                        observeHandoffCleanup,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(discardedHighTuple);
        Assert.NotNull(discardedLowTuple);
        Assert.True(discardedTuplesWereInvalidated);
        string liveHighTuple = generator.NodeHelpers[highLoaderKey];
        string liveLowTuple = generator.NodeHelpers[lowLoaderKey];
        Assert.NotEqual(discardedHighTuple, liveHighTuple);
        Assert.NotEqual(discardedLowTuple, liveLowTuple);
        AssertLoaderTupleIsLive(workflow, liveHighTuple);
        AssertLoaderTupleIsLive(workflow, liveLowTuple);
        Assert.Equal(2, NodesOfClass(bridge, "WanFirstLastFrameToVideo").Count());
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(2, samplers.Length);
        ComfyNode high = Assert.Single(samplers, IsHighNoiseSampler);
        ComfyNode low = AssertLowNoiseForHigh(samplers, high);

        Assert.Equal(11, high.FindInput("steps").LiteralAsInt());
        Assert.Equal(4.5, high.FindInput("cfg").LiteralAsDouble());
        Assert.Equal("euler", high.FindInput("sampler_name").LiteralAsString());
        Assert.Equal("normal", high.FindInput("scheduler").LiteralAsString());
        Assert.Equal(0, high.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(
            WanStageSchedulePolicy.HostHighEndStep(11, 0.6),
            high.FindInput("end_at_step").LiteralAsInt());
        Assert.Equal("enable", high.FindInput("return_with_leftover_noise").LiteralAsString());
        Assert.Equal("enable", high.FindInput("add_noise").LiteralAsString());
        Assert.Equal(43, high.FindInput("noise_seed").LiteralAsLong());

        Assert.Equal(14, low.FindInput("steps").LiteralAsInt());
        Assert.Equal(8.25, low.FindInput("cfg").LiteralAsDouble());
        Assert.Equal("dpmpp_2m", low.FindInput("sampler_name").LiteralAsString());
        Assert.Equal("karras", low.FindInput("scheduler").LiteralAsString());
        Assert.Equal((int)Math.Round(14 * (1 - 0.6)), low.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(10000, low.FindInput("end_at_step").LiteralAsInt());
        Assert.Equal("disable", low.FindInput("return_with_leftover_noise").LiteralAsString());
        Assert.Equal("disable", low.FindInput("add_noise").LiteralAsString());
        Assert.Equal(44, low.FindInput("noise_seed").LiteralAsLong());
        Assert.Same(high, low.FindInput("latent_image").Connection?.Node);

        AssertSamplerModelSource(bridge, high, models.VideoModel.Name);
        AssertSamplerModelSource(bridge, low, lowNoiseModel.Name);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        Assert.False(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                VideoStagesExtension.SectionIdForStage(0)));
        Assert.True(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                T2IParamInput.SectionID_VideoSwap));
    }

    [Fact]
    public void Wan_authored_LoRA_is_high_only_and_host_swap_LoRA_is_low_only()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = models.VideoModel;
        AddLoraModel("UnitTest_Wan_High_Authored.safetensors");
        AddLoraModel("UnitTest_Wan_Low_Host.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 11);
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_Wan_High_Authored",
            ["weight"] = 0.4,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage));
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);
        input.Set(
            T2IParamTypes.Loras,
            new List<string> { "UnitTest_Wan_Low_Host" });
        input.Set(
            T2IParamTypes.LoraWeights,
            new List<string> { "0.9" });
        input.Set(
            T2IParamTypes.LoraTencWeights,
            new List<string> { "0.7" });
        input.Set(
            T2IParamTypes.LoraSectionConfinement,
            new List<string> { $"{T2IParamInput.SectionID_VideoSwap}" });

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode high = Assert.Single(samplers, IsHighNoiseSampler);
        ComfyNode low = AssertLowNoiseForHigh(samplers, high);
        ComfyNode authored = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_High_Authored.safetensors");
        ComfyNode hostSwap = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Low_Host.safetensors");
        Assert.True(ModelBranchReaches(bridge, high, authored));
        Assert.False(ModelBranchReaches(bridge, high, hostSwap));
        Assert.True(ModelBranchReaches(bridge, low, hostSwap));
        Assert.False(ModelBranchReaches(bridge, low, authored));
        Assert.Equal(0.4, authored.FindInput("strength_model").LiteralAsDouble().Value, 6);
        Assert.Equal(0.9, hostSwap.FindInput("strength_model").LiteralAsDouble().Value, 6);
        Assert.Equal(
            ["UnitTest_Wan_Low_Host"],
            input.Get(T2IParamTypes.Loras));
        Assert.Equal(["0.9"], input.Get(T2IParamTypes.LoraWeights));
        Assert.Equal(["0.7"], input.Get(T2IParamTypes.LoraTencWeights));
        Assert.Equal(
            [$"{T2IParamInput.SectionID_VideoSwap}"],
            input.Get(T2IParamTypes.LoraSectionConfinement));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_prompt_LoRA_is_high_only_and_same_model_host_swap_LoRA_is_low_only()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_High_Prompt.safetensors");
        AddLoraModel("UnitTest_Wan_Low_Prompt_Test.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 11);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage),
            prompt:
                "global <videoclip[0,0]><lora:UnitTest_Wan_High_Prompt:0.4>");
        ConfigureSwap(input, models.VideoModel, percent: 0.6);
        input.Set(
            T2IParamTypes.Loras,
            new List<string> { "UnitTest_Wan_Low_Prompt_Test" });
        input.Set(
            T2IParamTypes.LoraWeights,
            new List<string> { "0.9" });
        input.Set(
            T2IParamTypes.LoraTencWeights,
            new List<string> { "0.7" });
        input.Set(
            T2IParamTypes.LoraSectionConfinement,
            new List<string> { $"{T2IParamInput.SectionID_VideoSwap}" });
        LoraParamState original = null;
        WorkflowGenerator.WorkflowGenStep snapshot = new(
            g => original = CaptureLoraParams(g.UserInput),
            Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(snapshot));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode high = Assert.Single(samplers, IsHighNoiseSampler);
        ComfyNode low = AssertLowNoiseForHigh(samplers, high);
        ComfyNode promptLora = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_High_Prompt.safetensors");
        ComfyNode hostSwap = Assert.Single(
            LoraLoaderNodesOf(bridge),
            node => node.FindInput("lora_name").LiteralAsString()
                == "UnitTest_Wan_Low_Prompt_Test.safetensors");
        Assert.True(ModelBranchReaches(bridge, high, promptLora));
        Assert.False(ModelBranchReaches(bridge, high, hostSwap));
        Assert.True(ModelBranchReaches(bridge, low, hostSwap));
        Assert.False(ModelBranchReaches(bridge, low, promptLora));
        Assert.NotNull(original);
        AssertLoraParamsEqual(original, CaptureLoraParams(input));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Two_Wan_swap_clips_keep_authored_high_settings_and_share_global_low_settings()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            steps: 11,
            cfgScale: 4,
            sampler: "euler",
            scheduler: "normal");
        JObject second = MakeStage(
            models.VideoModel.Name,
            steps: 17,
            cfgScale: 6.5,
            sampler: "dpmpp_2m",
            scheduler: "karras");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(first), MakeClip(second)).ToString());
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(4, samplers.Length);
        ComfyNode[] highs = [.. samplers.Where(IsHighNoiseSampler)];
        Assert.Equal(2, highs.Length);
        ComfyNode firstHigh = Assert.Single(
            highs,
            node => node.FindInput("steps").LiteralAsInt() == 11);
        ComfyNode secondHigh = Assert.Single(
            highs,
            node => node.FindInput("steps").LiteralAsInt() == 17);
        AssertSamplerSettings(firstHigh, 11, 4, "euler", "normal");
        AssertSamplerSettings(secondHigh, 17, 6.5, "dpmpp_2m", "karras");
        ComfyNode firstLow = AssertLowNoiseForHigh(samplers, firstHigh);
        ComfyNode secondLow = AssertLowNoiseForHigh(samplers, secondHigh);
        AssertSamplerSettings(firstLow, 14, 8.25, "dpmpp_2m", "karras");
        AssertSamplerSettings(secondLow, 14, 8.25, "dpmpp_2m", "karras");
        Assert.Equal(6, firstLow.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(6, secondLow.FindInput("start_at_step").LiteralAsInt());
        foreach (ComfyNode high in highs)
        {
            AssertSamplerModelSource(bridge, high, models.VideoModel.Name);
        }
        AssertSamplerModelSource(bridge, firstLow, lowNoiseModel.Name);
        AssertSamplerModelSource(bridge, secondLow, lowNoiseModel.Name);
        Assert.Single(
            NodesOfClass(bridge, "CheckpointLoaderSimple"),
            node => node.FindInput("ckpt_name").LiteralAsString() == models.VideoModel.Name);
        Assert.Single(
            NodesOfClass(bridge, "CheckpointLoaderSimple"),
            node => node.FindInput("ckpt_name").LiteralAsString() == lowNoiseModel.Name);

        string highLoaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        string lowLoaderKey = $"modelloader_{lowNoiseModel.Name}_image2video";
        AssertLoaderTupleIsLive(workflow, generator.NodeHelpers[highLoaderKey]);
        AssertLoaderTupleIsLive(workflow, generator.NodeHelpers[lowLoaderKey]);
        Assert.False(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                VideoStagesExtension.SectionIdForStage(0)));
        Assert.False(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                VideoStagesExtension.SectionIdForStage(1)));
        Assert.Equal(26, generator.CurrentMedia.Frames);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Wan_swap_percent_endpoints_build_the_host_defined_split(double percent)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = WanInput(models, steps: 10);
        ConfigureSwap(input, lowNoiseModel, percent, lowSteps: 12);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode high = Assert.Single(samplers, IsHighNoiseSampler);
        ComfyNode low = AssertLowNoiseForHigh(samplers, high);

        Assert.Equal(
            WanStageSchedulePolicy.HostHighEndStep(10, percent),
            high.FindInput("end_at_step").LiteralAsInt());
        Assert.Equal((int)Math.Round(12 * (1 - percent)), low.FindInput("start_at_step").LiteralAsInt());
    }

    [Fact]
    public void Wan_clip_publishes_decoded_video_with_the_timeline_dimensions()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        (JObject workflow, WorkflowGenerator generator) = GenerateWanClip(models, steps: 6);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        VideoModelProfileDescriptor profile =
            WanArchitectureModule.Instance.Descriptor.Profiles.Single(
                candidate => candidate.Id
                    == WanArchitectureModule.ImageToVideoProfileId);
        // The host asked for 16 frames; Wan generates whole latent frames, so the graph makes 13
        // according to its resolved profile metadata, and the artifact reports what it makes
        // rather than what was asked for.
        Assert.Equal(
            StaticGeneratedFrameGrid.SnapDown(16, profile.FrameGrid),
            generator.CurrentMedia.Frames);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        // Timeline publication is architecture-neutral, so nothing downstream can branch on Wan.
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
    }

    [Fact]
    public void Wan_clip_preserves_an_aligned_authored_generated_frame_count()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 6));
        clip["duration"] = 0.5;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject _, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());

        VideoModelProfileDescriptor profile =
            WanArchitectureModule.Instance.Descriptor.Profiles.Single(
                candidate => candidate.Id
                    == WanArchitectureModule.ImageToVideoProfileId);
        Assert.True(StaticGeneratedFrameGrid.IsAligned(17, profile.FrameGrid));
        Assert.Equal(17, generator.CurrentMedia.Frames);
    }

    [Fact]
    public void Hard_cut_clips_each_generate_from_the_host_image_and_save_the_intermediate()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(stage), MakeClip(stage)).ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);

        // Both clips enter from the same still with the same prompt and length, so they share one
        // conditioning node and differ only in the seed each samples with.
        ComfyNode imageToVideo = Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        long[] seeds = [.. bridge.Graph.FindDownstream(imageToVideo.FindOutput(2))
            .Select(node => node.FindInput("noise_seed").LiteralAsLong().Value)
            .Order()];
        Assert.Equal([1L + 42, 1L + 43], seeds);

        // Two clips means clip 0's video is not the timeline's own output, so the host's
        // intermediate-images setting has something to save alongside the merged timeline.
        ComfyNode merged = Assert.Single(NodesOfClass(bridge, BatchImagesNodeNode.ClassType));
        ComfyNode[] saves = [.. NodesOfClass(bridge, "SwarmSaveAnimationWS")];
        Assert.Equal(2, saves.Length);
        Assert.Single(saves, node => node.FindInput("images").Connection?.Node == merged);
    }

    [Fact]
    public void Hard_cut_Wan_clips_may_execute_different_canonical_profiles()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel five = AddWan5bModel("UnitTest_Wan22_5b.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(
                MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10)),
                MakeClip(MakeStage(five.Name, "Generated", steps: 11))).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode fourteenLatent = Assert.Single(
            NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode fiveLatent = Assert.Single(
            NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
        ComfyNode fourteenSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode fiveSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("steps").LiteralAsInt() == 11);
        Assert.True(ReachesUpstream(bridge, fourteenSampler, fourteenLatent.Id));
        Assert.Same(fiveLatent, fiveSampler.FindInput("latent_image").Connection?.Node);
        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged, fourteenSampler.Id));
        Assert.True(ReachesUpstream(bridge, merged, fiveSampler.Id));
        Assert.Equal(26, generator.CurrentMedia.Frames);
        AssertLoaderTupleIsLive(
            workflow,
            generator.NodeHelpers[
                $"modelloader_{models.VideoModel.Name}_image2video"]);
        AssertLoaderTupleIsLive(
            workflow,
            generator.NodeHelpers[$"modelloader_{five.Name}_image2video"]);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData("ltx2", false)]
    [InlineData("ltx2", true)]
    [InlineData("wan22", false)]
    [InlineData("wan22", true)]
    public void Mixed_hard_cut_keeps_sourced_Wan_and_Ltx_provenance_audio_and_publication_isolated(
        string firstFamily,
        bool doNotSave)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel first = firstFamily == "wan22"
            ? models.WanVideoModel
            : models.LtxVideoModel;
        JObject ltxClip = MakeClip(
            MakeStage(models.LtxVideoModel.Name, "Generated", steps: 7));
        JObject wanClip = MakeWanSourcedClip(
            models.WanVideoModel.Name,
            control: 0.5,
            steps: 9);
        JObject document = firstFamily == "wan22"
            ? MakeDocument(wanClip, ltxClip)
            : MakeDocument(ltxClip, wanClip);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            first,
            document.ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);
        input.Set(T2IParamTypes.DoNotSave, doNotSave);
        string rootImageId = null;
        WorkflowGenerator.WorkflowGenStep captureRoot = new(g =>
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            rootImageId = bridge.ResolvePath(g.CurrentMedia.Path)?.Node.Id;
            Assert.NotNull(rootImageId);
        }, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([captureRoot, WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode wanSampler = Assert.Single(
            samplers,
            node => node.FindInput("positive").Connection?.Node?.ClassTypeName
                == "WanImageToVideo");
        ComfyNode ltxSampler = Assert.Single(
            samplers,
            node => node.FindInput("positive").Connection?.Node?.ClassTypeName
                != "WanImageToVideo");
        VAEEncodeNode wanInput = Assert.IsType<VAEEncodeNode>(
            wanSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, wanInput, sourceWindow.Id));
        Assert.False(ReachesUpstream(bridge, wanInput, rootImageId));
        Assert.True(ReachesUpstream(bridge, ltxSampler, rootImageId));
        Assert.False(ReachesUpstream(bridge, ltxSampler, sourceWindow.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Same(merged, trim.Image.Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(29, generator.CurrentMedia.Frames);

        EmptyAudioNode wanSilence = Assert.Single(
            bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(17 / 24.0, wanSilence.Duration.LiteralAsDouble()!.Value, precision: 6);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        TrimAudioDurationNode finalAudioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Equal(4 / 24.0, finalAudioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(29 / 24.0, finalAudioTrim.Duration.LiteralAsDouble()!.Value, precision: 6);
        AudioConcatNode finalAudio = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            concat => ReachesUpstream(bridge, finalAudioTrim, concat.Id));
        ComfyNode wanAudio = firstFamily == "wan22"
            ? finalAudio.Audio1.Connection!.Node
            : finalAudio.Audio2.Connection!.Node;
        ComfyNode otherAudio = firstFamily == "wan22"
            ? finalAudio.Audio2.Connection!.Node
            : finalAudio.Audio1.Connection!.Node;
        Assert.Same(wanSilence, wanAudio);
        Assert.NotSame(wanSilence, otherAudio);
        Assert.True(ReachesUpstream(bridge, otherAudio, ltxSampler.Id));

        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        if (doNotSave)
        {
            Assert.Empty(saves);
        }
        else
        {
            Assert.Equal(2, saves.Length);
            Assert.Single(saves, save => ReferenceEquals(trim, save.Images.Connection?.Node));
            ComfyNode firstSampler = firstFamily == "wan22" ? wanSampler : ltxSampler;
            ComfyNode secondSampler = firstFamily == "wan22" ? ltxSampler : wanSampler;
            Assert.Single(
                saves,
                save => !ReferenceEquals(trim, save.Images.Connection?.Node)
                    && ReachesUpstream(bridge, save.Images.Connection?.Node, firstSampler.Id)
                    && !ReachesUpstream(bridge, save.Images.Connection?.Node, secondSampler.Id));
        }
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_Ltx_then_multi_stage_Wan_keeps_nested_and_timeline_lifecycles_separate(
        bool doNotSave)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.WanVideoModel, "UnitTest_Wan22_Low.safetensors");
        JObject document = MakeDocument(
            MakeClip(MakeStage(models.LtxVideoModel.Name, "Generated", steps: 7)),
            MakeClip(
                MakeStage(models.WanVideoModel.Name, "Generated", steps: 9),
                MakeStage(
                    models.WanVideoModel.Name,
                    "PreviousStage",
                    control: 0.8,
                    steps: 10)));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.LtxVideoModel,
            document.ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);
        input.Set(T2IParamTypes.DoNotSave, doNotSave);
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode[] wanHighs =
        [
            .. samplers.Where(node =>
                IsHighNoiseSampler(node)
                && node.FindInput("positive").Connection?.Node?.ClassTypeName
                    == "WanImageToVideo"),
        ];
        Assert.Equal(2, wanHighs.Length);
        ComfyNode firstWanHigh = Assert.Single(
            wanHighs,
            node => node.FindInput("noise_seed").LiteralAsLong() == 44);
        ComfyNode secondWanHigh = Assert.Single(
            wanHighs,
            node => node.FindInput("noise_seed").LiteralAsLong() == 45);
        ComfyNode firstWanLow = AssertLowNoiseForHigh(samplers, firstWanHigh);
        ComfyNode secondWanLow = AssertLowNoiseForHigh(samplers, secondWanHigh);
        ComfyNode ltxSampler = Assert.Single(
            samplers,
            node => !ReachesUpstream(
                bridge,
                node.FindInput("positive").Connection?.Node,
                firstWanHigh.FindInput("positive").Connection?.Node?.Id)
                && node.FindInput("positive").Connection?.Node?.ClassTypeName
                    != "WanImageToVideo"
                && !IsLowNoiseSampler(node));

        ComfyNode firstWanConditioning =
            firstWanHigh.FindInput("positive").Connection?.Node;
        Assert.False(ReachesUpstream(
            bridge,
            firstWanConditioning.FindInput("start_image").Connection?.Node,
            ltxSampler.Id));
        VAEEncodeNode secondWanInput = Assert.IsType<VAEEncodeNode>(
            secondWanHigh.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, secondWanInput, firstWanLow.Id));
        Assert.False(ReachesUpstream(bridge, secondWanInput, ltxSampler.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Same(merged, trim.Image.Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.True(ReachesUpstream(bridge, trim, secondWanLow.Id));

        EmptyAudioNode wanSilence = Assert.Single(
            bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(13 / 24.0, wanSilence.Duration.LiteralAsDouble()!.Value, precision: 6);
        TrimAudioDurationNode finalAudioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Equal(4 / 24.0, finalAudioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(25 / 24.0, finalAudioTrim.Duration.LiteralAsDouble()!.Value, precision: 6);
        AudioConcatNode finalAudio = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            concat => ReachesUpstream(bridge, finalAudioTrim, concat.Id));
        Assert.True(ReachesUpstream(
            bridge,
            finalAudio.Audio2.Connection?.Node,
            wanSilence.Id));
        Assert.False(ReachesUpstream(
            bridge,
            finalAudio.Audio1.Connection?.Node,
            wanSilence.Id));

        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        if (doNotSave)
        {
            Assert.Empty(saves);
        }
        else
        {
            Assert.Equal(3, saves.Length);
            Assert.Single(saves, save => ReferenceEquals(trim, save.Images.Connection?.Node));
            Assert.Contains(
                saves,
                save => ReachesUpstream(
                    bridge,
                    save.Images.Connection?.Node,
                    ltxSampler.Id));
            Assert.Contains(
                saves,
                save => ReachesUpstream(
                        bridge,
                        save.Images.Connection?.Node,
                        firstWanLow.Id)
                    && !ReachesUpstream(
                        bridge,
                        save.Images.Connection?.Node,
                        secondWanLow.Id));
        }
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// The host trims every image-to-video pass it builds, so a timeline that delegates to it once
    /// per clip has to drop those and keep only the trim over the finished timeline.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Global_frame_trim_is_applied_once_over_the_finished_timeline(int clipCount)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument([.. Enumerable.Range(0, clipCount).Select(_ => MakeClip(stage))])
                .ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);
        ComfyNode trim = Assert.Single(NodesOfClass(bridge, SwarmTrimFramesNode.ClassType));
        Assert.Equal(4, trim.FindInput("trim_start").LiteralAsInt());
        Assert.Equal(13 * clipCount - 4, generator.CurrentMedia.Frames);
        Assert.Equal(clipCount, SamplerNodes(bridge).Count(IsLowNoiseSampler));
    }

    /// <summary>
    /// These settings live in the host's video parameters rather than the authored clip document,
    /// so preflight is the only place they can be refused rather than ignored by this slice.
    /// </summary>
    [Fact]
    public void Global_creativity_is_refused_in_favor_of_clip_local_Wan_control()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        T2IParamInput input = WanInput(models, steps: 10);
        PreflightSnapshot snapshot = new();
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.5);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));
        Assert.Contains("refinement strength is clip-local", error.Message);
        Assert.DoesNotContain("request: VideoStages:", error.Message);
        snapshot.AssertUnchanged();
    }

    [Fact]
    public void Global_end_image_is_refused_before_mutation_for_two_Wan_clips()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(stage), MakeClip(stage)).ToString());
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "request-global and is ambiguous");
    }

    [Theory]
    [InlineData("swap", "not supported when any active Wan stage uses profile")]
    [InlineData("swap-passthrough", "not supported when any active Wan stage uses profile")]
    [InlineData("end-frame", "request-global and is ambiguous")]
    public void Wan5b_request_global_swap_and_end_frame_are_refused_before_mutation(
        string option,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        T2IParamInput input = option == "swap-passthrough"
            ? BuildNativeInput(
                models.BaseModel,
                models.VideoModel,
                MakeDocument(MakeWanSourcedClip(
                    models.VideoModel.Name,
                    control: 0,
                    steps: 10)).ToString())
            : WanInput(models, steps: 10);
        if (option.StartsWith("swap", StringComparison.Ordinal))
        {
            input.Set(T2IParamTypes.VideoSwapModel, models.VideoModel);
        }
        else
        {
            input.Set(
                T2IParamTypes.VideoEndFrame,
                new Image([0x05], MediaType.ImagePng));
        }

        AssertPreflightRefusalBeforeMutation(input, expectedReason);
    }

    [Theory]
    [InlineData("stage-payload-5b")]
    [InlineData("resolved-5b-cross-profile")]
    [InlineData("clip-payload-5b")]
    [InlineData("owner-missing")]
    [InlineData("no-generating-stage")]
    [InlineData("control-passthrough-mismatch")]
    [InlineData("model-payload-mismatch")]
    [InlineData("input-position-mismatch")]
    public void Global_end_image_preflight_rejects_forged_profile_and_owner_contracts(
        string corruption)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        (VideoExecutionPlan plan, ClipPlan validClip) =
            MakeGeneratedWanRuntimeContractPlan(models);
        StagePlan validStage = Assert.Single(validClip.Stages);
        ClipPlan forgedClip;
        if (corruption == "stage-payload-5b")
        {
            forgedClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        ArchitecturePayload = validStage.RequireWanPayload() with
                        {
                            ProfileId = WanArchitectureModule.Ti2v5bProfileId,
                        },
                    },
                ],
            };
        }
        else if (corruption == "resolved-5b-cross-profile")
        {
            forgedClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        ResolvedModel = validStage.ResolvedModel with
                        {
                            ModelProfileId = WanArchitectureModule.Ti2v5bProfileId,
                        },
                    },
                ],
            };
        }
        else if (corruption == "clip-payload-5b")
        {
            forgedClip = validClip with
            {
                ArchitecturePayload = validClip.RequireWanPayload() with
                {
                    ProfileId = WanArchitectureModule.Ti2v5bProfileId,
                },
            };
        }
        else if (corruption == "owner-missing")
        {
            forgedClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        ArchitecturePayload = validStage.RequireWanPayload() with
                        {
                            OwnsVideoEndFrame = false,
                        },
                    },
                ],
            };
        }
        else if (corruption == "no-generating-stage")
        {
            forgedClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        Input = StageInputKind.PreviousStage,
                        IsPassthrough = true,
                        ArchitecturePayload = validStage.RequireWanPayload() with
                        {
                            Control = 0,
                            OwnsVideoEndFrame = false,
                        },
                    },
                ],
            };
        }
        else if (corruption == "control-passthrough-mismatch")
        {
            forgedClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        IsPassthrough = false,
                        ArchitecturePayload = validStage.RequireWanPayload() with
                        {
                            Control = 0,
                            OwnsVideoEndFrame = true,
                        },
                    },
                ],
            };
        }
        else if (corruption == "model-payload-mismatch")
        {
            forgedClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        ResolvedModel = validStage.ResolvedModel with
                        {
                            ModelName = "forged-model-name.safetensors",
                        },
                    },
                ],
            };
        }
        else
        {
            forgedClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        ClipStageIndex = 1,
                        Input = StageInputKind.PreviousStage,
                    },
                ],
            };
        }
        VideoExecutionPlan forgedPlan = plan with { Clips = [forgedClip] };
        WorkflowGenerator generator = new()
        {
            Workflow = new JObject
            {
                ["sentinel"] = new JObject
                {
                    ["class_type"] = "UnitTest_Sentinel",
                    ["inputs"] = new JObject(),
                },
            },
            UserInput = new(null),
        };
        generator.UserInput.Set(
            T2IParamTypes.VideoEndFrame,
            new Image([0x06], MediaType.ImagePng));
        JObject before = (JObject)generator.Workflow.DeepClone();

        PlanDiagnostic refusal = Assert.Single(
            new WanExecutionAdapter(generator)
                .PreflightRequest(new(forgedPlan)),
            diagnostic =>
                diagnostic.Severity == PlanDiagnosticSeverity.Error
                && diagnostic.Message.Contains("'Video End Frame'"));

        Assert.Contains("canonical 14B ownership", refusal.Message);
        Assert.True(JToken.DeepEquals(before, generator.Workflow));
        Assert.Null(generator.CurrentMedia);
        Assert.Empty(generator.NodeHelpers);
        Assert.Empty(generator.UserInput.SectionParamOverrides);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0.5)]
    public void Global_end_image_belongs_only_to_the_final_generating_Wan_stage(
        double terminalControl)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 10),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: terminalControl,
                    steps: 12)));
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode firstConditioning = Assert.Single(
            NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode terminalConditioning = Assert.Single(
            NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode terminalSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 12);
        Assert.True(ReachesUpstream(bridge, firstSampler, firstConditioning.Id));
        Assert.True(ReachesUpstream(bridge, terminalSampler, terminalConditioning.Id));
        if (terminalControl < 1)
        {
            VAEEncodeNode terminalInput = Assert.IsType<VAEEncodeNode>(
                terminalSampler.FindInput("latent_image").Connection?.Node);
            Assert.True(ReachesUpstream(bridge, terminalInput, firstSampler.Id));
            Assert.False(ReachesUpstream(
                bridge,
                terminalInput,
                terminalConditioning.Id));
        }
        Assert.NotNull(terminalConditioning.FindInput("end_image").Connection);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        INodeOutput terminalArtifact = bridge.ResolvePath(generator.CurrentMedia.Path);
        Assert.NotNull(terminalArtifact);
        Assert.True(ReachesUpstream(bridge, terminalArtifact.Node, terminalSampler.Id));
    }

    [Fact]
    public void Global_end_image_owner_precedes_a_trailing_Wan_passthrough()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 10),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    steps: 12),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0,
                    steps: 13)));
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x02], MediaType.ImagePng));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        Assert.Single(NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.Equal(2, SamplerNodes(bridge).Count());
        Assert.DoesNotContain(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 13);
        ComfyNode terminalSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 12);
        INodeOutput finalOutput = bridge.ResolvePath(generator.CurrentMedia.Path);
        Assert.NotNull(finalOutput);
        Assert.True(ReachesUpstream(bridge, finalOutput.Node, terminalSampler.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Terminal_Wan_stage_swap_owns_both_Flf_branches_and_restores_host_scopes()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Terminal_Low.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 10),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 1,
                    steps: 12)));
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x03], MediaType.ImagePng));
        string highLoaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        string lowLoaderKey = $"modelloader_{lowNoiseModel.Name}_image2video";

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        Assert.Equal(2, NodesOfClass(bridge, "WanFirstLastFrameToVideo").Count());
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(4, samplers.Length);
        ComfyNode firstHigh = Assert.Single(
            samplers,
            sampler => IsHighNoiseSampler(sampler)
                && sampler.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode terminalHigh = Assert.Single(
            samplers,
            sampler => IsHighNoiseSampler(sampler)
                && sampler.FindInput("steps").LiteralAsInt() == 12);
        ComfyNode firstLow = AssertLowNoiseForHigh(samplers, firstHigh);
        ComfyNode terminalLow = AssertLowNoiseForHigh(samplers, terminalHigh);
        Assert.Equal(
            "WanImageToVideo",
            firstHigh.FindInput("positive").Connection?.Node?.ClassTypeName);
        Assert.Same(
            firstHigh.FindInput("positive").Connection?.Node,
            firstLow.FindInput("positive").Connection?.Node);
        Assert.Equal(
            "WanFirstLastFrameToVideo",
            terminalHigh.FindInput("positive").Connection?.Node?.ClassTypeName);
        Assert.Equal(
            "WanFirstLastFrameToVideo",
            terminalLow.FindInput("positive").Connection?.Node?.ClassTypeName);
        AssertLoaderTupleIsLive(workflow, generator.NodeHelpers[highLoaderKey]);
        AssertLoaderTupleIsLive(workflow, generator.NodeHelpers[lowLoaderKey]);
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(0),
            generator.UserInput.SectionParamOverrides.Keys);
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(1),
            generator.UserInput.SectionParamOverrides.Keys);
        Assert.True(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                T2IParamInput.SectionID_VideoSwap));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Global_end_image_is_refused_before_mutation_for_sourced_Wan_clip()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 1,
                steps: 10)).ToString());
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "ImageToVideo clip");
    }

    [Fact]
    public void Refine_video_entry_is_refused_before_mutation_for_Wan()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0x01, 0x02], MediaType.VideoMp4));
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x03], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "entry mode 'RefineVideo'");
    }

    [Fact]
    public void Global_end_image_is_refused_before_mutation_for_Wan_text_entry()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 10)));
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x04], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "entry mode 'TextToVideo'");
    }

    [Fact]
    public void Global_refine_source_is_refused_before_mutation_for_clip_local_sourced_Wan()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 1,
                steps: 10)).ToString());
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0x01, 0x02], MediaType.VideoMp4));
        input.Set(VideoStagesExtension.RefineSkipStages, 0);

        AssertPreflightRefusalBeforeMutation(
            input,
            "cannot coexist with a clip-local sourced Wan timeline");
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public void Global_end_image_is_refused_before_mutation_for_mixed_timeline(
        string firstFamily)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel first = firstFamily == "wan22"
            ? models.WanVideoModel
            : models.LtxVideoModel;
        T2IModel second = firstFamily == "wan22"
            ? models.LtxVideoModel
            : models.WanVideoModel;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            first,
            MakeDocument(
                MakeClip(MakeStage(first.Name, "Generated", steps: 7)),
                MakeClip(MakeStage(second.Name, "Generated", steps: 9))).ToString());
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "request-global and is ambiguous");
    }

    [Fact]
    public void Incompatible_Wan_swap_model_is_refused_before_graph_mutation()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoSwapModel, models.BaseModel);

        AssertPreflightRefusalBeforeMutation(input, "not a supported Wan 2.2");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Invalid_Wan_swap_percent_is_refused_before_graph_mutation(double swapPercent)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoSwapModel, lowNoiseModel);
        input.Set(T2IParamTypes.VideoSwapPercent, swapPercent);

        AssertPreflightRefusalBeforeMutation(input, "must be finite and between 0 and 1");
    }

    [Theory]
    [InlineData(10, 0.5, 0.7, 1)]
    [InlineData(12, 0.5, 0.5, 0)]
    public void Wan_swap_refuses_an_empty_later_stage_high_noise_window_before_mutation(
        int steps,
        double control,
        double swapPercent,
        int expectedComparison)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: control,
                    steps: steps)));
        ConfigureSwap(input, lowNoiseModel, swapPercent);

        int start = WanStageSchedulePolicy.StartStep(steps, control);
        int end = WanStageSchedulePolicy.HostHighEndStep(steps, swapPercent);
        Assert.Equal(expectedComparison, Math.Sign(start.CompareTo(end)));
        AssertPreflightRefusalBeforeMutation(input, "no high-noise sampling window");
    }

    [Fact]
    public void Wan_swap_refuses_an_empty_sourced_stage_zero_high_noise_window_before_mutation()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Source_Low.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanSourcedClip(
                models.VideoModel.Name,
                control: 0.5,
                steps: 10)).ToString());
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        AssertPreflightRefusalBeforeMutation(input, "no high-noise sampling window");
    }

    [Theory]
    [InlineData(0.9, 8, "quantizes to sampler start step 0")]
    public void Json_later_positive_partial_that_quantizes_to_zero_is_refused_before_mutation(
        double control,
        int steps,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(models.VideoModel.Name, steps: 8);
        JObject second = MakeStage(models.VideoModel.Name, control: control, steps: steps);
        first.Remove("imageReference");
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second));

        VideoStagesSpec parsed = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);
        AssertPreflightRefusalBeforeMutation(input, expectedReason);
    }

    [Theory]
    [InlineData(0.9, 8, false, 0.5, false, "quantizes to sampler start step 0")]
    [InlineData(0.5, 10, true, 0.7, false, "no high-noise sampling window")]
    [InlineData(0.9, 8, false, 0.5, true, "quantizes to sampler start step 0")]
    [InlineData(0.5, 10, true, 0.7, true, "no high-noise sampling window")]
    public void Decoded_stage_adapter_rechecks_schedule_invariants_before_media_access(
        double control,
        int steps,
        bool withSwap,
        double swapPercent,
        bool sourcedInput,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator generator = new()
        {
            Workflow = new JObject(),
            UserInput = new(null),
        };
        WanStagePayload payload = new(
            models.VideoModel.Name,
            WanArchitectureModule.ImageToVideoProfileId,
            control,
            steps,
            4.5,
            "euler",
            "normal",
            []);
        StagePlan stage = new(
            StageId: sourcedInput ? 0 : 1,
            ClipStageIndex: sourcedInput ? 0 : 1,
            ClipStageRawIndex: sourcedInput ? 0 : 1,
            sourcedInput ? StageInputKind.SourceVideo : StageInputKind.PreviousStage,
            IsPassthrough: false,
            payload,
            new(
                IsTimelineTerminal: true,
                IntermediateOutputPolicy.NotEligible,
                PreserveConfiguredAudioTrackSave: false));
        ClipPlan clip = new(
            ClipId: 0,
            Frames: 13,
            sourcedInput ? ClipInputKind.SourceVideo : ClipInputKind.RootMedia,
            IsSourced: sourcedInput,
            SourceVideo: sourcedInput
                ? new("data", "source.mp4", 0, 512, 512, 24)
                : null,
            [stage],
            Audio: null)
        {
            Architecture = WanArchitectureModule.Instance.Descriptor,
            EntryMode = sourcedInput
                ? ArchitectureEntryMode.SourceVideo
                : ArchitectureEntryMode.ImageToVideo,
            ArchitecturePayload = new WanClipPayload(
                0,
                WanArchitectureModule.ImageToVideoProfileId),
        };
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = generator,
            VideoSwapModel = withSwap ? models.VideoModel : null,
            VideoSwapPercent = swapPercent,
            Frames = 13,
            Steps = steps,
        };
        JObject before = (JObject)generator.Workflow.DeepClone();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new WanDecodedVideoStageInput(
                generator,
                (512, 512),
                24,
                new GlobalVideoFrameTrimmer(generator))
                .Configure(clip, stage, genInfo));

        Assert.Contains(expectedReason, error.Message);
        Assert.True(JToken.DeepEquals(before, generator.Workflow));
        Assert.Null(generator.CurrentMedia);
    }

    [Theory]
    [InlineData("entry", "entry mode, clip input, or source-video plan")]
    [InlineData("profile", "canonical Wan profile")]
    [InlineData("model", "canonical Wan profile")]
    [InlineData("payload-profile", "canonical Wan profile")]
    [InlineData("clip-profile", "canonical Wan profile")]
    [InlineData("loras-default", "invalid immutable normal-LoRA payload")]
    [InlineData("loras-no-op", "invalid immutable normal-LoRA payload")]
    [InlineData("passthrough-loras", "samplerless passthrough with a normal-LoRA plan")]
    [InlineData("end-frame-owner", "invalid request-global end-frame ownership")]
    public void Wan_runtime_clip_contract_rejects_stale_sourced_plans_before_graph_mutation(
        string corruption,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        (VideoExecutionPlan plan, ClipPlan validClip) =
            MakeSourcedWanRuntimeContractPlan(models);
        ClipPlan invalidClip;
        if (corruption == "entry")
        {
            invalidClip = validClip with
            {
                EntryMode = ArchitectureEntryMode.ImageToVideo,
            };
        }
        else if (corruption == "profile")
        {
            StagePlan validStage = Assert.Single(validClip.Stages);
            StagePlan staleStage = validStage with
            {
                ResolvedModel = validStage.ResolvedModel with
                {
                    ModelProfileId = new("stale-wan-profile"),
                },
            };
            invalidClip = validClip with { Stages = [staleStage] };
        }
        else if (corruption == "model")
        {
            StagePlan validStage = Assert.Single(validClip.Stages);
            WanStagePayload stalePayload = validStage.RequireWanPayload() with
            {
                Model = "stale-wan-model",
            };
            invalidClip = validClip with
            {
                Stages = [validStage with { ArchitecturePayload = stalePayload }],
            };
        }
        else if (corruption == "payload-profile")
        {
            StagePlan validStage = Assert.Single(validClip.Stages);
            WanStagePayload stalePayload = validStage.RequireWanPayload() with
            {
                ProfileId = WanArchitectureModule.Ti2v5bProfileId,
            };
            invalidClip = validClip with
            {
                Stages = [validStage with { ArchitecturePayload = stalePayload }],
            };
        }
        else if (corruption == "clip-profile")
        {
            WanClipPayload stalePayload = validClip.RequireWanPayload() with
            {
                ProfileId = WanArchitectureModule.Ti2v5bProfileId,
            };
            invalidClip = validClip with { ArchitecturePayload = stalePayload };
        }
        else if (corruption == "loras-default")
        {
            StagePlan validStage = Assert.Single(validClip.Stages);
            WanStagePayload stalePayload = validStage.RequireWanPayload() with
            {
                Loras = default,
            };
            invalidClip = validClip with
            {
                Stages = [validStage with { ArchitecturePayload = stalePayload }],
            };
        }
        else if (corruption == "loras-no-op")
        {
            StagePlan validStage = Assert.Single(validClip.Stages);
            WanStagePayload stalePayload = validStage.RequireWanPayload() with
            {
                Loras = [new("forged-no-op-lora", 0, 0)],
            };
            invalidClip = validClip with
            {
                Stages = [validStage with { ArchitecturePayload = stalePayload }],
            };
        }
        else if (corruption == "end-frame-owner")
        {
            StagePlan validStage = Assert.Single(validClip.Stages);
            WanStagePayload stalePayload = validStage.RequireWanPayload() with
            {
                OwnsVideoEndFrame = true,
            };
            invalidClip = validClip with
            {
                Stages = [validStage with { ArchitecturePayload = stalePayload }],
            };
        }
        else
        {
            StagePlan validStage = Assert.Single(validClip.Stages);
            WanStagePayload stalePayload = validStage.RequireWanPayload() with
            {
                Control = 0,
                Loras = [new("forged-passthrough-lora", 0.5, 0.5)],
            };
            invalidClip = validClip with
            {
                Stages =
                [
                    validStage with
                    {
                        IsPassthrough = true,
                        ArchitecturePayload = stalePayload,
                    },
                ],
            };
        }
        VideoExecutionPlan invalidPlan = plan with { Clips = [invalidClip] };
        WorkflowGenerator generator = new()
        {
            Workflow = new JObject
            {
                ["sentinel"] = new JObject
                {
                    ["class_type"] = "UnitTest_Sentinel",
                    ["inputs"] = new JObject(),
                },
            },
            UserInput = new(null),
            Features = [.. WanSourceFeatures],
        };
        JObject before = (JObject)generator.Workflow.DeepClone();
        using WanGenerationSession session = new(
            generator,
            invalidPlan,
            new WanRootSources(null, null),
            new WanStageHostScope(generator, invalidPlan));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => session.Execute(new(
                invalidClip,
                ClipIndex: 0,
                PreviousClip: null,
                PreviousClipOutput: null,
                PreviousTimelineClipOutput: null)));

        Assert.Contains("malformed plan", error.Message);
        Assert.Contains(expectedReason, error.Message);
        Assert.True(JToken.DeepEquals(before, generator.Workflow));
        Assert.Null(generator.CurrentMedia);
        Assert.Empty(generator.NodeHelpers);
        Assert.Empty(generator.UserInput.SectionParamOverrides);
    }

    [Fact]
    public void Wan_runtime_clip_contract_rejects_text_encoder_only_LoRA()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        (VideoExecutionPlan plan, ClipPlan clip) =
            MakeSourcedWanRuntimeContractPlan(models);
        StagePlan stage = Assert.Single(clip.Stages);
        WanStagePayload payload = stage.RequireWanPayload() with
        {
            Loras = [new("text-encoder-only", 0, 0.8)],
        };
        ClipPlan textOnlyClip = clip with
        {
            Stages = [stage with { ArchitecturePayload = payload }],
        };
        VideoExecutionPlan textOnlyPlan = plan with { Clips = [textOnlyClip] };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WanRuntimeClipContract.Validate(textOnlyPlan, textOnlyClip));

        Assert.Contains("malformed plan", error.Message);
        Assert.Contains("invalid immutable normal-LoRA payload", error.Message);
    }

    [Theory]
    [InlineData("missing-media", "marker is missing")]
    [InlineData("malformed-media", "marker is malformed")]
    [InlineData("removed-media-node", "was removed")]
    [InlineData("missing-snapshot", "snapshot is missing")]
    public void Corrupt_pre_core_handoff_fails_closed_and_cleans_every_Wan_key(
        string corruption,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator captured = null;
        WanRuntimeKeyScope keys = new();
        WorkflowGenerator.WorkflowGenStep remember =
            new(g => captured = g, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1);
        WorkflowGenerator.WorkflowGenStep corrupt = new(g =>
        {
            if (corruption == "missing-media")
            {
                g.NodeHelpers.Remove(keys.PreCoreMedia);
            }
            else if (corruption == "malformed-media")
            {
                g.NodeHelpers[keys.PreCoreMedia] = "not-a-marker";
            }
            else if (corruption == "missing-snapshot")
            {
                g.NodeHelpers.Remove(keys.PreCoreNodeIds);
            }
            else
            {
                string nodeId = g.NodeHelpers[keys.PreCoreMedia]
                    .Split(VideoGraphHelpers.MarkerSeparator)[0];
                using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
                bridge.RemoveNode(nodeId);
            }
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        remember,
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        corrupt,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains(expectedReason, error.Message);
        Assert.DoesNotContain(
            captured.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_refuses_an_unresolvable_root_without_leaving_partial_Wan_keys()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator captured = null;
        WorkflowGenerator.WorkflowGenStep breakRoot = new(g =>
        {
            captured = g;
            g.CurrentMedia = g.CurrentMedia.WithPath(new JArray("removed-root", 0));
        }, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.01);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([breakRoot, WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains("host root image is missing or no longer resolves", error.Message);
        Assert.DoesNotContain(
            captured.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
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

    private static (JObject Workflow, WorkflowGenerator Generator) GenerateWanClip(
        TestModelBundle models,
        int steps) =>
        WorkflowTestHarness.GenerateWithStepsAndState(WanInput(models, steps), WanSteps());

    private static T2IParamInput WanInput(TestModelBundle models, int steps) =>
        BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated", steps: steps)));

    private static JObject MakeWanSourcedClip(
        string model,
        double control,
        int steps,
        params JObject[] laterStages)
    {
        JObject first = MakeStage(model, "Generated", control: control, steps: steps);
        first.Remove("imageReference");
        JObject clip = MakeClip([first, .. laterStages]);
        clip["duration"] = WanSourcedDuration;
        clip["sourceVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,"
                + Convert.ToBase64String([0x11, 0x22, 0x33]),
            ["fileName"] = "wan-source.mp4",
            ["startSeconds"] = WanSourcedStartSeconds,
        };
        return clip;
    }

    private static (VideoExecutionPlan Plan, ClipPlan Clip)
        MakeSourcedWanRuntimeContractPlan(TestModelBundle models)
    {
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor;
        ResolvedVideoModel resolved = new(
            models.VideoModel.Name,
            WanArchitectureModule.ArchitectureId,
            WanArchitectureModule.ImageToVideoProfileId,
            descriptor);
        StagePlan stage = new(
            StageId: 0,
            ClipStageIndex: 0,
            ClipStageRawIndex: 0,
            StageInputKind.SourceVideo,
            IsPassthrough: false,
            new WanStagePayload(
                models.VideoModel.Name,
                ProfileId: WanArchitectureModule.ImageToVideoProfileId,
                Control: 0.5,
                Steps: 10,
                CfgScale: 4.5,
                Sampler: "euler",
                Scheduler: "normal",
                Loras: []),
            new StageOutputPlan(
                IsTimelineTerminal: true,
                IntermediateOutputPolicy.NotEligible,
                PreserveConfiguredAudioTrackSave: false))
        {
            ResolvedModel = resolved,
        };
        ClipPlan clip = new(
            ClipId: 0,
            Frames: WanSourcedFrames,
            ClipInputKind.SourceVideo,
            IsSourced: true,
            new SourceVideoPlan(
                "data:video/mp4;base64,"
                    + Convert.ToBase64String([0x11, 0x22, 0x33]),
                "runtime-contract-source.mp4",
                StartSeconds: 0,
                TargetWidth: 512,
                TargetHeight: 512,
                TargetFramesPerSecond: 24),
            Stages: [stage],
            Audio: null)
        {
            Architecture = descriptor,
            EntryMode = ArchitectureEntryMode.SourceVideo,
            ArchitecturePayload = new WanClipPayload(
                0,
                WanArchitectureModule.ImageToVideoProfileId),
        };
        VideoExecutionPlan plan = new(
            Width: 512,
            Height: 512,
            FramesPerSecond: 24,
            new RootPlan(
                HostRootKind.ImageToVideo,
                RootUse.None,
                HostCoreDisposition.Drop,
                TimelineOutputDisposition.PublishTimelineOutput,
                NativeAudioDisposition.DiscardWithRoot),
            Clips: [clip],
            Boundaries: [],
            Diagnostics: []);
        return (plan, clip);
    }

    private static (VideoExecutionPlan Plan, ClipPlan Clip)
        MakeGeneratedWanRuntimeContractPlan(TestModelBundle models)
    {
        (VideoExecutionPlan plan, ClipPlan sourcedClip) =
            MakeSourcedWanRuntimeContractPlan(models);
        StagePlan sourcedStage = Assert.Single(sourcedClip.Stages);
        StagePlan generatedStage = sourcedStage with
        {
            Input = StageInputKind.RootMedia,
            ArchitecturePayload = sourcedStage.RequireWanPayload() with
            {
                Control = 1,
                OwnsVideoEndFrame = true,
            },
        };
        ClipPlan generatedClip = sourcedClip with
        {
            Input = ClipInputKind.RootMedia,
            IsSourced = false,
            SourceVideo = null,
            Stages = [generatedStage],
            EntryMode = ArchitectureEntryMode.ImageToVideo,
        };
        return (plan with { Clips = [generatedClip] }, generatedClip);
    }

    private static SwarmFrameWindowNode AssertWanSourceConformChain(
        WorkflowBridge bridge,
        int width,
        int height)
    {
        SwarmLoadVideoB64Node load = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(load, components.Video.Connection?.Node);
        SwarmVideoResampleFPSNode resample = Assert.Single(
            bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.Same(components, resample.ImagesInput.Connection?.Node);
        Assert.Same(components, resample.FpsIn.Connection?.Node);
        Assert.Equal(24.0, resample.FpsOut.LiteralAsDouble());
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Same(resample, window.ImagesInput.Connection?.Node);
        Assert.Equal(
            (int)Math.Round(WanSourcedStartSeconds * 24),
            window.StartFrame.LiteralAsInt());
        Assert.Equal(WanSourcedFrames, window.FrameCount.LiteralAsInt());
        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node == window);
        Assert.Equal(width, scale.Width.LiteralAsInt());
        Assert.Equal(height, scale.Height.LiteralAsInt());
        return window;
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

    private static ComfyNode SamplerBySeed(IEnumerable<ComfyNode> samplers, long seed) =>
        Assert.Single(
            samplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == seed);

    private static string NodeText(ComfyNode node) =>
        node?.FindInput("text")?.LiteralAsString()
        ?? node?.FindInput("prompt")?.LiteralAsString();

    private static bool IsHighNoiseSampler(ComfyNode node) =>
        node is not null
        && node.FindInput("add_noise").LiteralAsString() == "enable"
        && node.FindInput("return_with_leftover_noise").LiteralAsString() == "enable";

    private static bool IsLowNoiseSampler(ComfyNode node) =>
        node is not null
        && node.FindInput("add_noise").LiteralAsString() == "disable"
        && node.FindInput("return_with_leftover_noise").LiteralAsString() == "disable";

    private static ComfyNode AssertLowNoiseForHigh(
        IEnumerable<ComfyNode> samplers,
        ComfyNode high) =>
        Assert.Single(
            samplers,
            node => IsLowNoiseSampler(node)
                && node.FindInput("latent_image").Connection?.Node == high);

    private static void AssertSamplerSettings(
        ComfyNode sampler,
        int steps,
        double cfg,
        string samplerName,
        string scheduler)
    {
        Assert.Equal(steps, sampler.FindInput("steps").LiteralAsInt());
        Assert.Equal(cfg, sampler.FindInput("cfg").LiteralAsDouble());
        Assert.Equal(samplerName, sampler.FindInput("sampler_name").LiteralAsString());
        Assert.Equal(scheduler, sampler.FindInput("scheduler").LiteralAsString());
    }

    private static void ConfigureSwap(
        T2IParamInput input,
        T2IModel lowNoiseModel,
        double percent,
        int lowSteps = 14)
    {
        input.Set(T2IParamTypes.VideoSwapModel, lowNoiseModel);
        input.Set(T2IParamTypes.VideoSwapPercent, percent);
        input.Set(T2IParamTypes.Steps, lowSteps, T2IParamInput.SectionID_VideoSwap);
        input.Set(T2IParamTypes.CFGScale, 8.25, T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SamplerParam,
            "dpmpp_2m",
            T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SchedulerParam,
            "karras",
            T2IParamInput.SectionID_VideoSwap);
    }

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

    private static void EnableHostLoraLoading()
    {
        WorkflowGenerator.AddModelGenStep(g =>
        {
            int confinement = g.IsImageToVideoSwap
                ? T2IParamInput.SectionID_VideoSwap
                : T2IParamInput.SectionID_Video;
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(
                confinement,
                g.LoadingModel,
                g.LoadingClip);
        }, -10);
    }

    private static T2IModel AddLoraModel(string name)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            handler = new() { ModelType = "LoRA" };
            Program.T2IModelSets["LoRA"] = handler;
        }
        T2IModel model = new(handler, "/tmp", $"/tmp/{name}", name);
        handler.Models[model.Name] = model;
        return model;
    }

    private static bool ModelBranchReaches(
        WorkflowBridge bridge,
        ComfyNode sampler,
        ComfyNode expectedUpstream) =>
        ReachesUpstream(
            bridge,
            sampler.FindInput("model").Connection?.Node,
            expectedUpstream.Id);

    private static T2IModel AddDistinctWanModel(T2IModel recognizedModel, string name)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, "/tmp", $"/tmp/{name}", name)
        {
            ModelClass = recognizedModel.ModelClass,
        };
        handler.Models[model.Name] = model;
        return model;
    }

    private static T2IModel AddWan5bModel(string name)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, "/tmp", $"/tmp/{name}", name)
        {
            ModelClass = new T2IModelClass
            {
                ID = WanArchitectureModule.Ti2v5bModelClassId,
                Name = "Wan 2.2 Text/Image2Video 5B",
                CompatClass = T2IModelClassSorter.CompatWan22_5b,
                StandardWidth = 960,
                StandardHeight = 960,
            },
        };
        handler.Models[model.Name] = model;
        return model;
    }

    private static void AssertSamplerModelSource(
        WorkflowBridge bridge,
        ComfyNode sampler,
        string expectedModel)
    {
        ComfyNode loader = Assert.Single(
            bridge.Graph.FindUpstream(sampler),
            node => node.ClassTypeName == "CheckpointLoaderSimple");
        Assert.Equal(expectedModel, loader.FindInput("ckpt_name").LiteralAsString());
    }

    private static void AssertLoaderTupleIsLive(JObject workflow, string tuple)
    {
        string[] parts = tuple.Split(':');
        Assert.Equal(6, parts.Length);
        foreach (string nodeId in new[] { parts[0], parts[2], parts[4] }
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId)))
        {
            Assert.True(workflow[nodeId] is JObject);
        }
    }

    private static void AssertPreflightRefusalBeforeMutation(
        T2IParamInput input,
        string expectedReason)
    {
        PreflightSnapshot snapshot = new();
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));
        Assert.Contains(expectedReason, error.Message);
        snapshot.AssertUnchanged();
    }
}
