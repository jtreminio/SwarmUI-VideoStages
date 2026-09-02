using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Authoring;

namespace VideoStages;

// Hide request-global passes that VideoStages applies per clip or timeline.
internal static class CoreWorkflowCompatibility
{
    private static readonly ConditionalWeakTable<
        WorkflowGenerator,
        List<Action<T2IParamInput>>> Restorations = new();

    internal static void Register()
    {
        WorkflowGenerator.AddStep(
            PrepareMedia,
            Constants.WorkflowStepPriority.PrepareCoreMedia);
        WorkflowGenerator.AddStep(
            SuppressSeedVr,
            Constants.WorkflowStepPriority.SuppressCoreSeedVr);
        WorkflowGenerator.AddStep(
            Restore,
            Constants.WorkflowStepPriority.RestoreCoreSeedVr);
        WorkflowGenerator.AddStep(
            SuppressFinalCleanup,
            Constants.WorkflowStepPriority.SuppressCoreFinalCleanup);
        WorkflowGenerator.AddStep(
            Restore,
            Constants.WorkflowStepPriority.RestoreCoreFinalCleanup);
    }

    private static void PrepareMedia(WorkflowGenerator generator)
    {
        if (!DocumentJson.IsActive(generator.UserInput)
            || generator.CurrentMedia is null)
        {
            return;
        }
        // Core's pre-save decodes for its save without updating this tracker.
        generator.CurrentMedia = generator.CurrentMedia.DecodeLatents(
            generator.CurrentVae,
            null);
    }

    private static void SuppressSeedVr(WorkflowGenerator generator)
    {
        if (!DocumentJson.IsActive(generator.UserInput))
        {
            return;
        }
        Suppress(generator, ComfyUIBackendExtension.SeedVRModel);
    }

    private static void SuppressFinalCleanup(WorkflowGenerator generator)
    {
        if (!DocumentJson.IsActive(generator.UserInput))
        {
            return;
        }
        Suppress(generator, T2IParamTypes.TrimVideoStartFrames);
        Suppress(generator, T2IParamTypes.TrimVideoEndFrames);
        Suppress(generator, ComfyUIBackendExtension.VideoFrameInterpolationMethod);
        Suppress(generator, ComfyUIBackendExtension.VideoFrameInterpolationMultiplier);
    }

    private static void Suppress<T>(
        WorkflowGenerator generator,
        T2IRegisteredParam<T> parameter)
    {
        if (!generator.UserInput.TryGet(parameter, out T value))
        {
            return;
        }
        Restorations.GetOrCreateValue(generator).Add(
            input => input.Set(parameter, value));
        generator.UserInput.Remove(parameter);
    }

    private static void Restore(WorkflowGenerator generator)
    {
        if (!Restorations.TryGetValue(generator, out List<Action<T2IParamInput>> restore))
        {
            return;
        }
        foreach (Action<T2IParamInput> action in restore)
        {
            action(generator.UserInput);
        }
        Restorations.Remove(generator);
    }
}
