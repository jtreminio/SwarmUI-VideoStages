using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Ltx2;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// Pins the request-preflight phase: a request whose architecture dependencies cannot be resolved is
/// rejected before any VideoStages phase touches the host graph, and the gate stays inert when the
/// extension is toggled off.
/// </summary>
[Collection("VideoStagesTests")]
public sealed class RequestPreflightPhaseTests
{
    /// <summary>
    /// Runs between the last host step that precedes VideoStages and the preflight phase itself, so
    /// the captured state is exactly what every VideoStages phase must leave alone on failure.
    /// </summary>
    private sealed class PhaseBoundarySnapshot
    {
        internal WorkflowGenerator Generator { get; private set; }

        internal JObject Workflow { get; private set; }

        internal WGNodeData CurrentMedia { get; private set; }

        internal Dictionary<string, string> NodeHelpers { get; private set; }

        internal WorkflowGenerator.WorkflowGenStep Step() =>
            new(g =>
            {
                Generator = g;
                Workflow = (JObject)g.Workflow.DeepClone();
                CurrentMedia = g.CurrentMedia;
                NodeHelpers = new(g.NodeHelpers);
            }, Constants.WorkflowStepPriority.PreflightRequest - 0.5);

        internal void AssertUnmutated()
        {
            Assert.NotNull(Generator);
            Assert.True(
                JToken.DeepEquals(Workflow, Generator.Workflow),
                "A VideoStages phase mutated the workflow graph before preflight rejected the "
                + "request.");
            Assert.Same(CurrentMedia, Generator.CurrentMedia);
            Assert.Equal(NodeHelpers, Generator.NodeHelpers);
        }
    }

    private static JObject IcLoraClip(TestModelBundle models)
    {
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "UnitTest_PreflightIcLora",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["driveData"] = "Visual",
            ["strength"] = 1.0,
            ["attentionStrength"] = 1.0,
            ["controlType"] = Constants.IcLoraControlNone,
            ["driveMedia"] = new JObject
            {
                ["data"] = "data:video/mp4;base64,QUJD",
                ["fileName"] = "drive.mp4",
            },
        });
        return clip;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> FullPhaseSequence(
        PhaseBoundarySnapshot snapshot) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    // Regression: the IC-LoRA custom-node dependency used to be discovered by the stage runner at
    // priority 11.5, long after the capture and drop-core phases had rewritten the graph and removed
    // the host's core video output. The user was left with a broken workflow instead of a clean error.
    [Fact]
    public void Missing_ic_lora_nodes_fail_the_request_before_any_phase_mutates_the_workflow()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterPreflightIcLora();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            new JArray(IcLoraClip(models)).ToString());
        PhaseBoundarySnapshot snapshot = new();

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                FullPhaseSequence(snapshot),
                features: ["variation_seed"]));

        Assert.Contains(Ltx2HostIntegration.NodeUrl, error.Message);
        snapshot.AssertUnmutated();
    }

    // The same request is a normal generation once the custom nodes are present, so preflight must
    // not be a blanket rejection of IC-LoRA timelines.
    [Fact]
    public void Preflight_passes_the_same_request_when_the_ic_lora_nodes_are_installed()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterPreflightIcLora();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            new JArray(IcLoraClip(models)).ToString());
        PhaseBoundarySnapshot snapshot = new();

        JObject workflow = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            FullPhaseSequence(snapshot)).Workflow;

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Assert.NotEmpty(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
    }

    // A toggled-off VideoStages group must not start rejecting other people's generations: the
    // identical config JSON is still sent, but the Enabled gate flag is absent.
    [Fact]
    public void Preflight_does_not_fire_when_the_video_stages_group_is_disabled()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            new JArray(IcLoraClip(models)).ToString());
        input.Remove(VideoStagesExtension.Enabled);
        PhaseBoundarySnapshot snapshot = new();

        JObject workflow = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            FullPhaseSequence(snapshot),
            features: ["variation_seed"]).Workflow;

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Assert.Empty(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
    }

    private static void RegisterPreflightIcLora()
    {
        if (!SwarmUI.Core.Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            handler = new T2IModelHandler() { ModelType = "LoRA" };
            SwarmUI.Core.Program.T2IModelSets["LoRA"] = handler;
        }
        T2IModel lora = TestStubModel.Create(handler, "UnitTest_PreflightIcLora.safetensors");
        handler.Models[lora.Name] = lora;
    }
}
