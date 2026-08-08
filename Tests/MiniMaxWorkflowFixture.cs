using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution.Graph;

namespace VideoStages.Tests;

internal sealed class MiniMaxWorkflowFixture : VideoStagesWorkflowFixture
{
    public const string ModelFixturePath =
        "models/diffusion_models/MiniMaxH3-Workflow-Test.safetensors";

    public const int Steps = 8;

    /// <summary>MiniMax H3 is a guidance-distilled model; production runs it at cfg 1.</summary>
    public const double CfgScale = 1;

    /// <summary>25 requested frames snap up to 39 on H3's 17k+5 grid.</summary>
    public const int GeneratedFrames = 39;

    private MiniMaxWorkflowFixture(bool withBaseModel)
        : base([ModelFixturePath], withBaseModel)
    {
    }

    public static MiniMaxWorkflowFixture Create() => new(withBaseModel: false);

    public static MiniMaxWorkflowFixture CreateWithBaseModel() => new(withBaseModel: true);

    protected override void InstallSupportModels() =>
        TestModelFactory.InstallMiniMaxSupportModels();

    public override int DefaultSteps => Steps;

    public override double DefaultCfgScale => CfgScale;

    public override int ExpectedGeneratedFrames => GeneratedFrames;

    /// <summary>
    /// Stands in for core's ControlNet branch, which no MiniMax POST shape builds: one
    /// <c>GetVideoComponents</c> source per occupied ControlNet slot, for
    /// <c>ControlNetCoreMediaCapture</c> to find. The loader and apply nodes are scaffolding for
    /// the capture and are torn down again once it has run, so the graph stays orphan-free.
    /// </summary>
    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> SeedControlNetVideoSources(
        int count = 1,
        int firstIndex = 0) =>
    [
        new(g =>
        {
            UnitTestStubs.EnsureComfyControlNetParamsRegistered();
            T2IModelHandler handler = new() { ModelType = "ControlNet" };
            using WorkflowBridge bridge = BridgeSync.For(g);
            for (int index = firstIndex; index < firstIndex + count; index++)
            {
                T2IModel model = TestStubModel.Create(
                    handler,
                    $"UnitTest_MiniMax_ControlNet_{index}.safetensors");
                g.UserInput.Set(T2IParamTypes.Controlnets[index].Strength, 0.8);
                g.UserInput.Set(T2IParamTypes.Controlnets[index].Model, model);
                GetVideoComponentsNode components = bridge.AddNode(
                    new GetVideoComponentsNode(),
                    $"90{index + 1}");
                ControlNetLoaderNode loader = bridge.AddNode(
                    new ControlNetLoaderNode().With(
                        ControlNetName: model.ToString(g.ModelFolderFormat)),
                    $"91{index + 1}");
                ControlNetApplyAdvancedNode apply = new();
                apply.ControlNet.ConnectTo(loader.CONTROLNET);
                apply.Image.ConnectTo(components.Images);
                bridge.AddNode(apply, $"92{index + 1}");
            }
        }, Constants.WorkflowStepPriority.ControlNetPreprocessors - 0.01),
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            for (int index = firstIndex; index < firstIndex + count; index++)
            {
                VideoGraphHelpers.RemoveNode(g, bridge, $"92{index + 1}");
                VideoGraphHelpers.RemoveNode(g, bridge, $"91{index + 1}");
            }
        }, Constants.WorkflowStepPriority.ControlNetPreprocessors + 0.01),
    ];
}
