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
using VideoStages.HostVideo;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// End-to-end tests for WAN workflows generated through SwarmUI's image-to-video path.
/// </summary>
[Collection("VideoStagesTests")]
public class WanRuntimeFlowTests
{
    private const int WanInitVideoFrames = 17;
    private const double WanInitVideoDuration = 0.6;
    private const double WanInitVideoStartSeconds = 1;
    private static readonly string[] WanSourceFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];
    private static readonly string[] WanReferenceFeatures = WanSourceFeatures;

    private sealed class PreflightSnapshot
    {
        internal WorkflowGenerator Generator { get; private set; }
        internal JObject Workflow { get; private set; }
        internal WGNodeData Media { get; private set; }
        internal Dictionary<string, string> NodeHelpers { get; private set; }
        internal Dictionary<int, Dictionary<string, object>> SectionOverrides
        {
            get;
            private set;
        }

        internal WorkflowGenerator.WorkflowGenStep Step() => new(g =>
        {
            Generator = g;
            Workflow = (JObject)g.Workflow.DeepClone();
            Media = g.CurrentMedia;
            NodeHelpers = new(g.NodeHelpers);
            SectionOverrides = g.UserInput.SectionParamOverrides.ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<string, object>(pair.Value.ValuesInput));
        }, Constants.WorkflowStepPriority.PreflightRequest - 0.5);

        internal void AssertUnchanged()
        {
            Assert.True(JToken.DeepEquals(Workflow, Generator.Workflow));
            Assert.Same(Media, Generator.CurrentMedia);
            Assert.Equal(NodeHelpers, Generator.NodeHelpers);
            Assert.Equal(
                SectionOverrides.Keys.Order(),
                Generator.UserInput.SectionParamOverrides.Keys.Order());
            foreach ((int sectionId, Dictionary<string, object> values) in
                SectionOverrides)
            {
                Assert.Equal(
                    values,
                    Generator.UserInput.SectionParamOverrides[sectionId].ValuesInput);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wan_clip_generates_from_the_host_image_and_replaces_the_core_video(
        bool useWan21ImageModel)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        if (useWan21ImageModel)
        {
            models.VideoModel.ModelClass = models.VideoModel.ModelClass with
            {
                ID = "wan-2_1-image2video-14b",
                Name = "Wan 2.1 Image2Video 14B",
            };
        }
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

        // The handoff prunes the host loader nodes and tuple before WAN builds its clip graph.
        Assert.NotNull(hostLoaderTuple);
        Assert.Equal(6, hostLoaderTuple.Split(':').Length);
        Assert.True(hostLoaderTupleWasInvalidated);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);

        // The host conditioning node is pruned, leaving only the clip's node.
        ComfyNode imageToVideo = Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode sampler = Assert.Single(bridge.Graph.FindDownstream(imageToVideo.FindOutput(2)));
        Assert.Equal(10, sampler.FindInput("steps").LiteralAsInt());

        // The clip generates at the timeline resolution.
        Assert.Single(
            NodesOfClass(bridge, "ImageScale"),
            node => node.FindInput("width").LiteralAsInt() == 512
                && node.FindInput("height").LiteralAsInt() == 512);

        Assert.DoesNotContain(
            generator.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_Wan_swap_fields_warn_are_preserved_and_never_build_a_hidden_second_pass()
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

        int hostSamplerCount = -1;
        WorkflowGenerator.WorkflowGenStep captureHostSamplers = new(g =>
        {
            using WorkflowBridge hostBridge = WorkflowBridge.Create(g.Workflow);
            hostSamplerCount = SamplerNodes(hostBridge).Count();
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostSamplers,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(1, hostSamplerCount);
        Assert.Single(SamplerNodes(bridge));
        Assert.False(generator.IsImageToVideoSwap);
        Assert.Single(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.wan-video-swap-ignored");
        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Single(
            warnings,
            warning => warning.Contains(
                "separate high-noise and low-noise timeline stages",
                StringComparison.Ordinal));

        LegacyVideoSwapRequestSnapshot snapshot =
            generator.GetVideoStagesSpec().LegacyVideoSwap;
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
    public void Wan5b_text_entry_builds_native_latent_without_an_image_donor()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 10)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode latent = Assert.Single(
            NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
        Assert.False(latent.FindInput("start_image").HasValue);
        Assert.Equal(25, latent.FindInput("length").LiteralAsInt());
        Assert.Equal(512, latent.FindInput("width").LiteralAsInt());
        Assert.Equal(512, latent.FindInput("height").LiteralAsInt());
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Same(latent, sampler.FindInput("latent_image").Connection?.Node);
        Assert.Equal(10, sampler.FindInput("steps").LiteralAsInt());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath(generator.CurrentMedia.Path)?.Node,
            sampler.Id));
        Assert.DoesNotContain(
            NodesOfClass(bridge, "WanImageToVideo"),
            _ => true);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Authored_first_frame_upload_conditions_a_text_root_without_using_a_root_donor()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["refs"] = new JArray(UploadedWanReference("RklSU1Q=", fromEnd: false));
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeDocument(clip).ToString());
        WorkflowGenerator.WorkflowGenStep clearUnusedRootDonor = new(g =>
        {
            g.CurrentMedia = null;
            g.CurrentVae = null;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(clearUnusedRootDonor),
                WanReferenceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("RklSU1Q=", upload.ImageBase64.LiteralAsString());
        ComfyNode conditioning = Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.FindInput("start_image").Connection?.Node,
            upload.Id));
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.True(ReachesUpstream(bridge, sampler, conditioning.Id));
        Assert.DoesNotContain(NodesOfClass(bridge, "EmptyHunyuanLatentVideo"), _ => true);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData("missing", "missing inline data and a file name")]
    [InlineData("malformed", "Ignoring invalid WAN first-frame reference payload")]
    public void Unusable_first_upload_warns_and_falls_back_to_native_text_without_a_root_donor(
        string failure,
        string expectedWarning)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject reference = new()
        {
            ["source"] = "Upload",
            ["frame"] = 1,
            ["fromEnd"] = false,
        };
        if (failure == "malformed")
        {
            reference["uploadedImage"] = new JObject
            {
                ["data"] = "not-an-image-payload",
                ["fileName"] = "broken.png",
            };
        }
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["refs"] = new JArray(reference);
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeDocument(clip).ToString());
        WorkflowGenerator.WorkflowGenStep clearUnusedRootDonor = new(g =>
        {
            g.CurrentMedia = null;
            g.CurrentVae = null;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(clearUnusedRootDonor),
                WanReferenceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ClipPlan planned = Assert.Single(
            generator.RequireVideoExecutionPlanContext().Plan.Clips);
        Assert.Equal(ClipInputKind.EmptyLatent, planned.Input);
        Assert.Equal(StageInputKind.EmptyLatent, Assert.Single(planned.Stages).Input);
        Assert.Single(NodesOfClass(bridge, "EmptyHunyuanLatentVideo"));
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));
        Assert.Empty(NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.Contains(
            Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]),
            warning => warning.Contains(expectedWarning, StringComparison.Ordinal));
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("wan-2_1-text2video-14b", true)]
    public void Authored_last_only_upload_conditions_a_text_root_without_a_root_donor(
        string modelClassId,
        bool expectsClipVision)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        if (modelClassId is not null)
        {
            models.VideoModel.ModelClass = models.VideoModel.ModelClass with
            {
                ID = modelClassId,
                Name = "Wan 2.1 Video 14B",
            };
        }
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["refs"] = new JArray(UploadedWanReference("TEFTVA==", fromEnd: true));
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeDocument(clip).ToString());
        WorkflowGenerator.WorkflowGenStep clearUnusedRootDonor = new(g =>
        {
            g.CurrentMedia = null;
            g.CurrentVae = null;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(clearUnusedRootDonor),
                WanReferenceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode conditioning = Assert.Single(
            NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.False(conditioning.FindInput("start_image").HasValue);
        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.FindInput("end_image").Connection?.Node,
            upload.Id));
        Assert.Equal(
            expectsClipVision,
            conditioning.FindInput("clip_vision_end_image").HasValue);
        Assert.False(conditioning.FindInput("clip_vision_start_image").HasValue);
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));
        Assert.Empty(NodesOfClass(bridge, "EmptyHunyuanLatentVideo"));
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Same(
            conditioning,
            sampler.FindInput("latent_image").Connection?.Node);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Malformed_first_with_valid_last_degrades_to_last_only_conditioning()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject malformedFirst = UploadedWanReference(
            "not-valid-base64",
            fromEnd: false);
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["refs"] = new JArray(
            malformedFirst,
            UploadedWanReference("TEFTVA==", fromEnd: true));
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeDocument(clip).ToString());
        WorkflowGenerator.WorkflowGenStep clearUnusedRootDonor = new(g =>
        {
            g.CurrentMedia = null;
            g.CurrentVae = null;
        }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps().Append(clearUnusedRootDonor),
                WanReferenceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode conditioning = Assert.Single(
            NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.False(conditioning.FindInput("start_image").HasValue);
        Assert.True(conditioning.FindInput("end_image").HasValue);
        Assert.Contains(
            Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]),
            warning => warning.Contains(
                "Ignoring invalid WAN first-frame reference payload",
                StringComparison.Ordinal));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Authored_last_frame_upload_belongs_to_the_terminal_generating_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeClip(
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
                steps: 13));
        clip["refs"] = new JArray(UploadedWanReference("TEFTVA==", fromEnd: true));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanReferenceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());
        ComfyNode firstConditioning = Assert.Single(
            NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode terminalConditioning = Assert.Single(
            NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.True(ReachesUpstream(
            bridge,
            terminalConditioning.FindInput("end_image").Connection?.Node,
            upload.Id));
        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode terminalSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 12);
        Assert.True(ReachesUpstream(bridge, firstSampler, firstConditioning.Id));
        Assert.False(ReachesUpstream(bridge, firstSampler, terminalConditioning.Id));
        Assert.True(ReachesUpstream(bridge, terminalSampler, terminalConditioning.Id));
        Assert.DoesNotContain(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 13);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Authored_first_and_last_uploads_are_clip_local_and_override_no_global_state()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["refs"] = new JArray(
            UploadedWanReference("RklSU1Q=", fromEnd: false),
            UploadedWanReference("TEFTVA==", fromEnd: true));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        Image globalEnd = new([0x44], MediaType.ImagePng);
        input.Set(T2IParamTypes.VideoEndFrame, globalEnd);

        (JObject workflow, _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanReferenceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadImageB64Node[] uploads =
            [.. bridge.Graph.NodesOfType<SwarmLoadImageB64Node>()];
        Assert.Equal(
            ["RklSU1Q=", "TEFTVA=="],
            uploads.Select(upload => upload.ImageBase64.LiteralAsString()).Order());
        ComfyNode conditioning = Assert.Single(
            NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        SwarmLoadImageB64Node first = Assert.Single(
            uploads,
            upload => upload.ImageBase64.LiteralAsString() == "RklSU1Q=");
        SwarmLoadImageB64Node last = Assert.Single(
            uploads,
            upload => upload.ImageBase64.LiteralAsString() == "TEFTVA==");
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.FindInput("start_image").Connection?.Node,
            first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.FindInput("end_image").Connection?.Node,
            last.Id));
        Assert.Same(globalEnd, input.Get(T2IParamTypes.VideoEndFrame, null));
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<LoadImageNode>(),
            load => load.Image.LiteralAsString() == "${videoendframe}");
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData("wan-2_1-image2video-14b")]
    [InlineData("wan-2_1-text2video-14b")]
    public void Wan21_text_entry_uses_the_host_empty_video_and_restores_global_frames(
        string modelClassId)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
            Name = "Wan 2.1 Video 14B",
        };
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["duration"] = 13.0 / 24.0;
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeDocument(clip).ToString());
        Assert.Equal(25, input.Get(T2IParamTypes.Text2VideoFrames));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode latent = Assert.Single(
            NodesOfClass(bridge, "EmptyHunyuanLatentVideo"));
        int authoredFrames = latent.FindInput("length").LiteralAsInt().Value;
        Assert.NotEqual(25, authoredFrames);
        Assert.True(StaticGeneratedFrameGrid.IsAligned(
            authoredFrames,
            WanArchitectureModule.FrameGrid));
        Assert.Equal(authoredFrames, generator.CurrentMedia.Frames);
        Assert.Equal(25, input.Get(T2IParamTypes.Text2VideoFrames));
        Assert.Single(SamplerNodes(bridge));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
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
                    int scopedFrames = input.Get(T2IParamTypes.Text2VideoFrames);
                    Assert.NotEqual(25, scopedFrames);
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

            SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
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
    public void Wan5b_text_entry_defaults_to_81_aligned_frames_when_the_host_value_is_absent()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 10)));
        input.Remove(T2IParamTypes.Text2VideoFrames);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode latent = Assert.Single(
            NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
        Assert.False(latent.FindInput("start_image").HasValue);
        Assert.Equal(81, latent.FindInput("length").LiteralAsInt());
        Assert.True(StaticGeneratedFrameGrid.IsAligned(
            81,
            WanArchitectureModule.FrameGrid));
        Assert.Equal(81, generator.CurrentMedia.Frames);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan5b_text_entry_chains_a_later_stage_from_the_first_decoded_video()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 10),
                MakeStage(models.VideoModel.Name, "PreviousStage", steps: 11)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] latents = [.. NodesOfClass(bridge, "Wan22ImageToVideoLatent")];
        Assert.Equal(2, latents.Length);
        ComfyNode native = Assert.Single(
            latents,
            latent => !latent.FindInput("start_image").HasValue);
        ComfyNode continuation = Assert.Single(
            latents,
            latent => latent.FindInput("start_image").HasValue);
        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode secondSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 11);
        Assert.Same(native, firstSampler.FindInput("latent_image").Connection?.Node);
        Assert.Same(
            continuation,
            secondSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge,
            continuation.FindInput("start_image").Connection?.Node,
            firstSampler.Id));
        Assert.Equal(25, generator.CurrentMedia.Frames);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan5b_init_video_multistage_partial_and_passthrough_preserve_decoded_provenance()
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
            MakeDocument(MakeWanInitVideoClip(
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
            HostVideoStageSchedulePolicy.StartStep(12, 0.5),
            second.FindInput("start_at_step").LiteralAsInt());
        VAEEncodeNode secondInput = Assert.IsType<VAEEncodeNode>(
            second.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, secondInput, first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath(generator.CurrentMedia.Path)?.Node,
            second.Id));
        Assert.Equal(WanInitVideoFrames, generator.CurrentMedia.Frames);
        ComfyNode retainedNativeLatent = Assert.Single(
            NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
        Assert.Same(firstLatent, retainedNativeLatent);
        Assert.NotEmpty(
            bridge.Graph.FindInputsConnectedTo(retainedNativeLatent.FindOutput(0)));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan5b_cleanup_uses_model_class_despite_forged_profile_aliases()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Wan_persisted_and_prompt_LoRAs_use_the_generic_model_only_loader(
        bool useFiveBProfile,
        bool nativeTextEntry)
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
        if (nativeTextEntry)
        {
            AddLoraModel("UnitTest_Wan5b_Base_Confined.safetensors");
        }
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
        string stagesJson = JsonSingleClipStages(stage);
        const string promptText =
            "global <videoclip[0,0]><lora:UnitTest_Wan5b_Prompt:0.4:0.8>"
            + "<lora:UnitTest_Wan5b_Prompt_ModelZero:0:0.9>";
        T2IParamInput input = nativeTextEntry
            ? BuildTextToVideoInput(
                models.VideoModel,
                stagesJson,
                prompt: promptText)
            : BuildNativeInput(
                models.BaseModel,
                models.VideoModel,
                stagesJson,
                prompt: promptText);
        if (nativeTextEntry)
        {
            input.Set(
                T2IParamTypes.Loras,
                new List<string> { "UnitTest_Wan5b_Base_Confined" });
            input.Set(
                T2IParamTypes.LoraWeights,
                new List<string> { "0.95" });
            input.Set(
                T2IParamTypes.LoraTencWeights,
                new List<string> { "0.85" });
            input.Set(
                T2IParamTypes.LoraSectionConfinement,
                new List<string> { $"{T2IParamInput.SectionID_BaseOnly}" });
        }
        LoraParamState original = null;
        WorkflowGenerator.WorkflowGenStep snapshot = new(
            g => original = CaptureLoraParams(g.UserInput),
            Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                (nativeTextEntry
                    ? WorkflowTestHarness.Template_BaseOnlyImage()
                        .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                        .Concat(WorkflowTestHarness.VideoStagesSteps())
                    : WanSteps())
                    .Append(snapshot));
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
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.True(ModelBranchReaches(bridge, sampler, persisted));
        if (nativeTextEntry)
        {
            Assert.DoesNotContain(
                LoraLoaderNodesOf(bridge),
                node => node.FindInput("lora_name").LiteralAsString()
                    == "UnitTest_Wan5b_Base_Confined.safetensors"
                    && ModelBranchReaches(bridge, sampler, node));
            ComfyNode latent = Assert.Single(
                NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
            Assert.False(latent.FindInput("start_image").HasValue);
            Assert.False(generator.IsImageToVideo);
            Assert.False(generator.IsImageToVideoSwap);
        }
        Assert.NotNull(original);
        AssertLoraParamsEqual(original, CaptureLoraParams(input));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan5b_native_text_restores_ambient_video_and_audio_scopes_after_model_prep_failure()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
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
    public void InitVideo_generating_Wan_stage_applies_its_clip_LoRA()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        AddLoraModel("UnitTest_Wan_Source_Lora.safetensors");
        JObject clip = MakeWanInitVideoClip(
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
    public void Two_authored_Wan_noise_stages_use_two_ordinary_passes_with_decoded_handoff()
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
            HostVideoStageSchedulePolicy.StartStep(12, 0.35),
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
    public void Wan_pixel_upscale_resizes_the_decoded_input_before_the_next_generating_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 10);
        first.Remove("imageReference");
        JObject second = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            upscale: 1.5,
            upscaleMethod: "pixel-lanczos",
            steps: 12);
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 43);
        ComfyNode secondSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 44);
        ImageScaleNode authoredScale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 768
                && scale.Height.LiteralAsInt() == 768
                && scale.UpscaleMethod.LiteralAsString() == "lanczos"
                && scale.Image.Connection?.Node is VAEDecodeNode decode
                && ReachesUpstream(bridge, decode, firstSampler.Id));
        VAEEncodeNode secondStageEncode = Assert.IsType<VAEEncodeNode>(
            secondSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge,
            secondStageEncode,
            authoredScale.Id));
        ComfyNode secondConditioning =
            secondSampler.FindInput("positive").Connection?.Node;
        Assert.True(ReachesUpstream(
            bridge,
            secondConditioning.FindInput("start_image").Connection?.Node,
            authoredScale.Id));
        Assert.Equal(768, generator.CurrentMedia.Width);
        Assert.Equal(768, generator.CurrentMedia.Height);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_pixel_upscale_is_the_output_of_a_samplerless_passthrough_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 10);
        first.Remove("imageReference");
        JObject passthrough = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0,
            upscale: 1.5,
            upscaleMethod: "pixel-bicubic",
            steps: 12);
        passthrough.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, passthrough));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        ImageScaleNode scale = Assert.IsType<ImageScaleNode>(
            bridge.ResolvePath(generator.CurrentMedia.Path)?.Node);
        Assert.Equal(768, scale.Width.LiteralAsInt());
        Assert.Equal(768, scale.Height.LiteralAsInt());
        Assert.Equal("bicubic", scale.UpscaleMethod.LiteralAsString());
        Assert.True(ReachesUpstream(bridge, scale, sampler.Id));
        Assert.Equal(768, generator.CurrentMedia.Width);
        Assert.Equal(768, generator.CurrentMedia.Height);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData("latent-bislerp", "bislerp")]
    [InlineData("latentmodel-unit-upscaler.safetensors", "unit-upscaler.safetensors")]
    public void Wan_latent_upscale_warns_without_emitting_an_invalid_pixel_scaler(
        string upscaleMethod,
        string invalidPixelMethod)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 10);
        first.Remove("imageReference");
        JObject second = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            upscale: 1.5,
            upscaleMethod: upscaleMethod,
            steps: 12);
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, SamplerNodes(bridge).Count());
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.UpscaleMethod.LiteralAsString() == invalidPixelMethod);
        Assert.Contains(
            Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]),
            warning => warning.Contains(
                $"uses unsupported upscale method '{upscaleMethod}'. Ignoring upscale.",
                StringComparison.Ordinal));
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Partial_init_video_Wan_stage_uses_conformed_video_for_conditioning_and_latent()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeWanInitVideoClip(
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
            HostVideoStageSchedulePolicy.StartStep(10, 0.5),
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
        Assert.Equal(WanInitVideoFrames, encodedFrames.Length.LiteralAsInt());
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
    public void InitVideo_Wan_without_optional_filename_materializes_and_runs()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeWanInitVideoClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 10);
        ((JObject)clip["initVideo"]).Remove("fileName");
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

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(
            bridge,
            512,
            512);
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.True(ReachesUpstream(bridge, sampler, sourceWindow.Id));
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Full_control_init_video_Wan_stage_uses_only_the_source_first_frame()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanInitVideoClip(
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

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(
            bridge,
            512,
            512);
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
    public void InitVideo_Wan_clip_prunes_the_real_host_core_lineage_and_publishes_only_source_result()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanInitVideoClip(
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
    public void InitVideo_Wan_stage_zero_passthrough_publishes_trimmed_source_without_a_sampler()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanInitVideoClip(
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

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(
            bridge,
            512,
            512,
            expectedFrames: 16);
        Assert.Empty(SamplerNodes(bridge));
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.True(ReachesUpstream(bridge, trim, sourceWindow.Id));
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(12, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Same(trim, save.Images.Connection?.Node);
        Assert.Null(save.Audio.Connection);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void InitVideo_Wan_passthrough_then_refine_consumes_source_and_publishes_intermediate()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanInitVideoClip(
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_and_init_video_Wan_clips_keep_root_and_source_provenance_isolated(
        bool initVideoFirst)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject generated = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 8));
        generated["duration"] = WanInitVideoDuration;
        JObject initVideoClip = MakeWanInitVideoClip(
            models.VideoModel.Name,
            control: 0.5,
            steps: 10);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(initVideoFirst
                ? [initVideoClip, generated]
                : [generated, initVideoClip]).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                WanSourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow = AssertWanSourceConformChain(bridge, 512, 512);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode generatedSampler = SamplerBySeed(samplers, initVideoFirst ? 44 : 43);
        ComfyNode initVideoSampler = SamplerBySeed(samplers, initVideoFirst ? 43 : 44);
        ComfyNode generatedConditioning =
            generatedSampler.FindInput("positive").Connection?.Node;
        ComfyNode initVideoConditioning =
            initVideoSampler.FindInput("positive").Connection?.Node;
        Assert.False(ReachesUpstream(
            bridge,
            generatedConditioning.FindInput("start_image").Connection?.Node,
            sourceWindow.Id));
        Assert.True(ReachesUpstream(
            bridge,
            initVideoConditioning.FindInput("start_image").Connection?.Node,
            sourceWindow.Id));
        VAEEncodeNode initVideoInput = Assert.IsType<VAEEncodeNode>(
            initVideoSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, initVideoInput, sourceWindow.Id));
        Assert.False(ReachesUpstream(bridge, initVideoInput, generatedSampler.Id));
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
        Assert.Equal(1, HostVideoStageSchedulePolicy.StartStep(
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
            HostVideoStageSchedulePolicy.StartStep(10, 0.5),
            second.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(
            HostVideoStageSchedulePolicy.StartStep(12, 0.25),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Single_Wan_clip_applies_the_end_image_only_to_its_authored_terminal_stage(
        bool useWan21ImageModel)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        if (useWan21ImageModel)
        {
            models.VideoModel.ModelClass = models.VideoModel.ModelClass with
            {
                ID = "wan-2_1-image2video-14b",
                Name = "Wan 2.1 Image2Video 14B",
            };
        }
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));
        string discardedHostConditioningId = null;
        WorkflowGenerator.WorkflowGenStep captureHostConditioning = new(g =>
        {
            discardedHostConditioningId = Assert.Single(
                ((JObject)g.Workflow).Properties(),
                property => property.Value["class_type"]?.ToString()
                    == "WanImageToVideo").Name;
            Assert.DoesNotContain(
                ((JObject)g.Workflow).Properties(),
                property => property.Value["class_type"]?.ToString()
                    == "WanFirstLastFrameToVideo");
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostConditioning,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(discardedHostConditioningId);
        Assert.Null(workflow[discardedHostConditioningId]);
        Assert.NotNull(input.Get(T2IParamTypes.VideoEndFrame, null));
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
    public void Wan_clip_publishes_decoded_video_with_the_timeline_dimensions()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        (JObject workflow, WorkflowGenerator generator) = GenerateWanClip(models, steps: 6);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        // WAN snaps the requested 16 frames to its 13-frame latent grid.
        Assert.Equal(
            StaticGeneratedFrameGrid.SnapDown(
                16,
                WanArchitectureModule.Instance.Descriptor.FrameGrid),
            generator.CurrentMedia.Frames);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        // Published timeline media does not retain architecture compatibility metadata.
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
    }

    [Fact]
    public void Wan_clip_uses_its_own_resolved_frame_grid()
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

        Assert.True(StaticGeneratedFrameGrid.IsAligned(
            13,
            WanArchitectureModule.Instance.Descriptor.FrameGrid));
        Assert.Equal(13, generator.CurrentMedia.Frames);
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

        // Identical clip inputs share conditioning but use different sampler seeds.
        ComfyNode imageToVideo = Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        long[] seeds = [.. bridge.Graph.FindDownstream(imageToVideo.FindOutput(2))
            .Select(node => node.FindInput("noise_seed").LiteralAsLong().Value)
            .Order()];
        Assert.Equal([1L + 42, 1L + 43], seeds);

        // Save clip 0 separately from the merged timeline.
        ComfyNode merged = Assert.Single(NodesOfClass(bridge, BatchImagesNodeNode.ClassType));
        ComfyNode[] saves = [.. NodesOfClass(bridge, "SwarmSaveAnimationWS")];
        Assert.Equal(2, saves.Length);
        Assert.Single(saves, node => node.FindInput("images").Connection?.Node == merged);
    }

    [Fact]
    public void Hard_cut_conforms_an_upscaled_Wan_clip_before_timeline_assembly()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 8);
        JObject upscale = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0,
            upscale: 2,
            upscaleMethod: "pixel-lanczos",
            steps: 8);
        JObject regular = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 9);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(
                MakeClip(first, upscale),
                MakeClip(regular)).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode stageScale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 1024
                && scale.Height.LiteralAsInt() == 1024
                && scale.UpscaleMethod.LiteralAsString() == "lanczos");
        ImageScaleNode timelineConform = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 512
                && scale.Height.LiteralAsInt() == 512
                && ReachesUpstream(bridge, scale, stageScale.Id));
        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(
            bridge,
            merged.Images[0].Connection?.Node,
            timelineConform.Id));
        Assert.False(ReachesUpstream(
            bridge,
            merged.Images[1].Connection?.Node,
            stageScale.Id));
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Hard_cut_Wan5b_text_clips_join_in_order_without_a_host_image_donor()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        JObject document = MakeDocument(
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10)),
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 11)));
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            document.ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        string hostCoreOutputId = null;
        WorkflowGenerator.WorkflowGenStep captureHostCore = new(g =>
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            hostCoreOutputId = bridge.ResolvePath(g.CurrentMedia.Path)?.Node.Id;
            Assert.NotNull(hostCoreOutputId);
        }, Constants.WorkflowStepPriority.CoreImageToVideo + 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostCore,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(hostCoreOutputId);
        Assert.False(workflow.ContainsKey(hostCoreOutputId));
        ComfyNode[] latents = [.. NodesOfClass(bridge, "Wan22ImageToVideoLatent")];
        ComfyNode latent = Assert.Single(latents);
        Assert.All(
            latents,
            item => Assert.False(item.FindInput("start_image").HasValue));
        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 10);
        ComfyNode secondSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 11);
        Assert.Same(latent, firstSampler.FindInput("latent_image").Connection?.Node);
        Assert.Same(latent, secondSampler.FindInput("latent_image").Connection?.Node);
        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(
            bridge,
            merged.Images[0].Connection?.Node,
            firstSampler.Id));
        Assert.False(ReachesUpstream(
            bridge,
            merged.Images[0].Connection?.Node,
            secondSampler.Id));
        Assert.True(ReachesUpstream(
            bridge,
            merged.Images[1].Connection?.Node,
            secondSampler.Id));
        Assert.False(ReachesUpstream(
            bridge,
            merged.Images[1].Connection?.Node,
            firstSampler.Id));
        Assert.Equal(new JArray(merged.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(50, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.DoesNotContain(
            NodesOfClass(bridge, "WanImageToVideo"),
            _ => true);
        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        Assert.Equal(2, saves.Length);
        Assert.Single(
            saves,
            save => ReferenceEquals(merged, save.Images.Connection?.Node));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyAudioNode>());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_Ltx_and_Wan5b_text_hard_cuts_keep_order_provenance_audio_and_cleanup(
        bool wanFirst)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel wan5b = AddWan5bModel("UnitTest_Wan22_Text_Mixed_5b.safetensors");
        JObject ltxClip = MakeClip(
            MakeStage(models.LtxVideoModel.Name, "Generated", steps: 7));
        JObject wanClip = MakeClip(
            MakeStage(wan5b.Name, "Generated", steps: 9));
        JObject document = wanFirst
            ? MakeDocument(wanClip, ltxClip)
            : MakeDocument(ltxClip, wanClip);
        T2IParamInput input = BuildTextToVideoInput(
            models.LtxVideoModel,
            document.ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                StageFlowTests.BuildNativeTextToVideoStepsWithPreCoreVideo(
                    attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.False(workflow.ContainsKey("200"));
        Assert.False(workflow.ContainsKey("201"));
        Assert.False(workflow.ContainsKey("202"));
        ComfyNode wanLatent = Assert.Single(
            NodesOfClass(bridge, "Wan22ImageToVideoLatent"));
        Assert.False(wanLatent.FindInput("start_image").HasValue);
        ComfyNode wanSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 9);
        ComfyNode ltxSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 7);
        Assert.Same(
            wanLatent,
            wanSampler.FindInput("latent_image").Connection?.Node);
        Assert.False(ReachesUpstream(bridge, ltxSampler, wanLatent.Id));
        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        ComfyNode firstSampler = wanFirst ? wanSampler : ltxSampler;
        ComfyNode secondSampler = wanFirst ? ltxSampler : wanSampler;
        Assert.True(ReachesUpstream(
            bridge,
            merged.Images[0].Connection?.Node,
            firstSampler.Id));
        Assert.False(ReachesUpstream(
            bridge,
            merged.Images[0].Connection?.Node,
            secondSampler.Id));
        Assert.True(ReachesUpstream(
            bridge,
            merged.Images[1].Connection?.Node,
            secondSampler.Id));
        Assert.False(ReachesUpstream(
            bridge,
            merged.Images[1].Connection?.Node,
            firstSampler.Id));
        Assert.Equal(new JArray(merged.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(50, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.Compat);

        EmptyAudioNode wanSilence = Assert.Single(
            bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        AudioConcatNode joinedAudio = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            audio => ReachesUpstream(
                bridge,
                bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path)?.Node,
                audio.Id));
        ComfyNode firstAudio = joinedAudio.Audio1.Connection?.Node;
        ComfyNode secondAudio = joinedAudio.Audio2.Connection?.Node;
        Assert.Equal(wanFirst, ReferenceEquals(wanSilence, firstAudio));
        Assert.Equal(!wanFirst, ReferenceEquals(wanSilence, secondAudio));
        Assert.True(ReachesUpstream(
            bridge,
            wanFirst ? secondAudio : firstAudio,
            ltxSampler.Id));

        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        Assert.Equal(2, saves.Length);
        Assert.Single(
            saves,
            save => ReferenceEquals(merged, save.Images.Connection?.Node));
        Assert.Single(
            saves,
            save => !ReferenceEquals(merged, save.Images.Connection?.Node)
                && ReachesUpstream(
                    bridge,
                    save.Images.Connection?.Node,
                    firstSampler.Id)
                && !ReachesUpstream(
                    bridge,
                    save.Images.Connection?.Node,
                    secondSampler.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
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
    public void Mixed_hard_cut_keeps_init_video_Wan_and_Ltx_provenance_audio_and_publication_isolated(
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
        JObject wanClip = MakeWanInitVideoClip(
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

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode[] wanSamplers =
        [
            .. samplers.Where(node =>
                node.FindInput("positive").Connection?.Node?.ClassTypeName
                    == "WanImageToVideo"),
        ];
        Assert.Equal(2, wanSamplers.Length);
        ComfyNode firstWan = Assert.Single(
            wanSamplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == 44);
        ComfyNode secondWan = Assert.Single(
            wanSamplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == 45);
        ComfyNode ltxSampler = Assert.Single(
            samplers,
            node => !ReachesUpstream(
                bridge,
                node.FindInput("positive").Connection?.Node,
                firstWan.FindInput("positive").Connection?.Node?.Id)
                && node.FindInput("positive").Connection?.Node?.ClassTypeName
                    != "WanImageToVideo");

        ComfyNode firstWanConditioning =
            firstWan.FindInput("positive").Connection?.Node;
        Assert.False(ReachesUpstream(
            bridge,
            firstWanConditioning.FindInput("start_image").Connection?.Node,
            ltxSampler.Id));
        VAEEncodeNode secondWanInput = Assert.IsType<VAEEncodeNode>(
            secondWan.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, secondWanInput, firstWan.Id));
        Assert.False(ReachesUpstream(bridge, secondWanInput, ltxSampler.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Same(merged, trim.Image.Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.True(ReachesUpstream(bridge, trim, secondWan.Id));

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
                        firstWan.Id)
                    && !ReachesUpstream(
                        bridge,
                        save.Images.Connection?.Node,
                        secondWan.Id));
        }
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Global_frame_trim_is_applied_once_over_the_finished_timeline(int clipCount)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument([.. Enumerable.Range(0, clipCount).Select(_ => MakeClip(stage))])
                .ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);
        ComfyNode trim = Assert.Single(NodesOfClass(bridge, SwarmTrimFramesNode.ClassType));
        Assert.Equal(4, trim.FindInput("trim_start").LiteralAsInt());
        Assert.Equal(13 * clipCount - 4, generator.CurrentMedia.Frames);
        Assert.Equal(clipCount, SamplerNodes(bridge).Count());
    }

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
    public void Global_end_image_warns_and_is_ignored_for_two_Wan_clips()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(stage), MakeClip(stage)).ToString());
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertEndFrameWarningAndIgnored(input);
    }

    [Fact]
    public void Wan5b_image_end_frame_warns_and_is_ignored()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(
            T2IParamTypes.VideoEndFrame,
            new Image([0x05], MediaType.ImagePng));

        AssertEndFrameWarningAndIgnored(input);
    }

    [Fact]
    public void Wan5b_native_text_end_frame_warns_and_is_ignored()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22Ti2v5bModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 10)));
        input.Set(
            T2IParamTypes.VideoEndFrame,
            new Image([0x07], MediaType.ImagePng));

        AssertEndFrameWarningAndIgnored(input);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(0.5, null)]
    [InlineData(1, "wan-2_1-text2video-14b")]
    public void Global_end_image_belongs_only_to_the_final_generating_Wan_stage(
        double terminalControl,
        string modelClassId)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        if (modelClassId is not null)
        {
            models.VideoModel.ModelClass = models.VideoModel.ModelClass with
            {
                ID = modelClassId,
                Name = "Wan 2.1 Video 14B",
            };
        }
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
    public void Global_end_image_warns_and_is_ignored_for_init_video_Wan_clip()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeWanInitVideoClip(
                models.VideoModel.Name,
                control: 1,
                steps: 10)).ToString());
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertEndFrameWarningAndIgnored(input);
    }

    [Fact]
    public void Wan14b_text_entry_warns_and_does_not_use_global_end_image()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 10)));
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x04], MediaType.ImagePng));

        AssertEndFrameWarningAndIgnored(input);
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public void Global_end_image_warns_for_mixed_timeline(
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

        (JObject workflow, _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Single(
            warnings,
            warning => warning.Contains(
                "'Video End Frame' was ignored",
                StringComparison.Ordinal));
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
        Assert.Empty(NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
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
        string keyPrefix = $"videostages.arch.{WanArchitectureModule.ArchitectureId}";
        string preCoreMediaKey = $"{keyPrefix}.pre-core.media";
        string preCoreNodeIdsKey = $"{keyPrefix}.pre-core-node-ids";
        WorkflowGenerator.WorkflowGenStep remember =
            new(g => captured = g, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1);
        WorkflowGenerator.WorkflowGenStep corrupt = new(g =>
        {
            if (corruption == "missing-media")
            {
                g.NodeHelpers.Remove(preCoreMediaKey);
            }
            else if (corruption == "malformed-media")
            {
                g.NodeHelpers[preCoreMediaKey] = "not-a-marker";
            }
            else if (corruption == "missing-snapshot")
            {
                g.NodeHelpers.Remove(preCoreNodeIdsKey);
            }
            else
            {
                string nodeId = g.NodeHelpers[preCoreMediaKey]
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

        Assert.Contains("host root media is missing or no longer resolves", error.Message);
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

    private static JObject UploadedWanReference(string payload, bool fromEnd) =>
        new()
        {
            ["source"] = "Upload",
            ["frame"] = 1,
            ["fromEnd"] = fromEnd,
            ["uploadedImage"] = new JObject
            {
                ["data"] = $"data:image/png;base64,{payload}",
                ["fileName"] = fromEnd ? "last.png" : "first.png",
            },
        };

    private static SwarmFrameWindowNode AssertWanSourceConformChain(
        WorkflowBridge bridge,
        int width,
        int height,
        int expectedFrames = WanInitVideoFrames)
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
            (int)Math.Round(WanInitVideoStartSeconds * 24),
            window.StartFrame.LiteralAsInt());
        Assert.Equal(expectedFrames, window.FrameCount.LiteralAsInt());
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
            if (g.LoadingModelType == "negative"
                && !g.UserInput.Get(T2IParamTypes.NegativeModelIncludeLoras, true))
            {
                return;
            }
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(
                -1,
                g.LoadingModel,
                g.LoadingClip);
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(
                0,
                g.LoadingModel,
                g.LoadingClip);
            int confinement = g.IsRefinerStage
                ? T2IParamInput.SectionID_Refiner
                : g.IsPixelDecoderStage
                    ? T2IParamInput.SectionID_PixelDecoder
                    : g.IsImageToVideo
                        ? T2IParamInput.SectionID_Video
                        : T2IParamInput.SectionID_BaseOnly;
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

    private static void AssertEndFrameWarningAndIgnored(T2IParamInput input)
    {
        Image rawEndFrame = input.Get(T2IParamTypes.VideoEndFrame, null);
        (JObject workflow, _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Single(
            warnings,
            warning => warning.Contains(
                "'Video End Frame' was ignored",
                StringComparison.Ordinal));
        Assert.Empty(NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.Same(rawEndFrame, input.Get(T2IParamTypes.VideoEndFrame, null));
    }
}
