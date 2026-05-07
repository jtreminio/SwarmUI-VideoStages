using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Tests;

/// <summary>
/// Minimal harness to run only the VideoStages workflow steps in tests.
/// </summary>
internal static class WorkflowTestHarness
{
    private static readonly object LockObj = new();
    private static bool _initialized;
    private static List<WorkflowGenerator.WorkflowGenStep> _coreSteps = [];
    private static List<WorkflowGenerator.WorkflowGenStep> _videoStagesSteps = [];

    private static void EnsureInitialized()
    {
        lock (LockObj)
        {
            if (_initialized)
            {
                return;
            }

            List<WorkflowGenerator.WorkflowGenStep> before = [.. WorkflowGenerator.Steps];
            if (T2IParamTypes.Width is null)
            {
                T2IParamTypes.RegisterDefaults();
            }

            UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
            UnitTestStubs.EnsureComfyVideoParamsRegistered();

            VideoStagesExtension extension = new();
            extension.OnPreInit();
            extension.OnInit();

            List<WorkflowGenerator.WorkflowGenStep> after = [.. WorkflowGenerator.Steps];
            _coreSteps = before;
            _videoStagesSteps = after.Where(step => !before.Contains(step)).ToList();
            WorkflowGenerator.Steps = before;

            if (_videoStagesSteps.Count == 0)
            {
                throw new InvalidOperationException("VideoStages did not register any WorkflowGenerator steps during init.");
            }

            _initialized = true;
        }
    }

    public static IReadOnlyList<WorkflowGenerator.WorkflowGenStep> VideoStagesSteps()
    {
        EnsureInitialized();
        return _videoStagesSteps;
    }

    public static WorkflowGenerator.WorkflowGenStep CoreImageToVideoStep()
    {
        EnsureInitialized();
        return _coreSteps.Single(step => step.Priority == 11);
    }

    public static WorkflowGenerator.WorkflowGenStep CorePreVideoSavePrepStep()
    {
        EnsureInitialized();
        return _coreSteps.Single(step => step.Priority == 10);
    }

    public static JObject GenerateWithSteps(
        T2IParamInput input,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps,
        IEnumerable<string> features = null)
    {
        return GenerateWithStepsAndState(input, steps, features).Workflow;
    }

    public static (JObject Workflow, WorkflowGenerator Generator) GenerateWithStepsAndState(
        T2IParamInput input,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps,
        IEnumerable<string> features = null)
    {
        EnsureInitialized();

        List<WorkflowGenerator.WorkflowGenStep> priorSteps = [.. WorkflowGenerator.Steps];
        try
        {
            WorkflowGenerator.Steps = [.. steps.OrderBy(step => step.Priority)];
            input.ApplyLateSpecialLogic();

            WorkflowGenerator generator = new()
            {
                UserInput = input,
                Features = features is null ? [Constants.LtxVideoFeatureFlag, "variation_seed"] : [.. features],
                ModelFolderFormat = "/"
            };

            JObject workflow = generator.Generate();
            return (workflow, generator);
        }
        finally
        {
            WorkflowGenerator.Steps = priorSteps;
        }
    }

    public static WorkflowGenerator.WorkflowGenStep MinimalGraphSeedStep() =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);

            UnknownNode model = bridge.AddStub("UnitTest_Model", "4").WithOutputs("MODEL", "CLIP", "VAE");
            UnknownNode latent = bridge.AddStub("UnitTest_Latent", "10").WithOutputs("LATENT");

            g.CurrentModel = model.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = model.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);
            g.CurrentVae = model.GetOutput(2).ToWGNodeData(g, WGNodeData.DT_VAE);
            g.CurrentMedia = latent.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_LATENT_IMAGE);
            g.FinalLoadedModel = g.UserInput.Get(T2IParamTypes.Model, null);
            g.FinalLoadedModelList = g.FinalLoadedModel is null ? [] : [g.FinalLoadedModel];
        }, -1000);

    public static WorkflowGenerator.WorkflowGenStep DecodeSamplesToImageStep() =>
        new(g =>
        {
            if (g.CurrentMedia is null || g.CurrentVae is null)
            {
                return;
            }

            WGNodeData decoded = g.CurrentMedia.DecodeLatents(g.CurrentVae, false);
            decoded.Width = g.UserInput.GetImageWidth();
            decoded.Height = g.UserInput.GetImageHeight();
            g.CurrentMedia = decoded;
            g.BasicInputImage = decoded;
        }, -950);

    public static WorkflowGenerator.WorkflowGenStep SaveCurrentMediaStep() =>
        new(g =>
        {
            if (g.CurrentMedia is not null)
            {
                g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, "9");
            }
        }, 10);

    public static IEnumerable<WorkflowGenerator.WorkflowGenStep> Template_BaseOnlyImage() =>
        new[]
        {
            MinimalGraphSeedStep(),
            DecodeSamplesToImageStep()
        };
}
