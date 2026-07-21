using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// Host-param gating probes for the VideoStages pipeline — the questions that require inspecting
/// the live <see cref="WorkflowGenerator"/> (or mutating-and-restoring it) rather than the parsed
/// spec. Kept alongside <see cref="VideoStagesPromptSection"/>'s enable/active gate and out of the
/// parser so the parser stays a pure JSON→spec compiler.
/// </summary>
internal static class VideoStagesGate
{
    public static bool HasUsableVideoModel(WorkflowGenerator g, VideoStagesSpec spec)
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel)
            && videoModel is not null)
        {
            return true;
        }
        if (g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true)
        {
            return true;
        }
        foreach (ClipSpec clip in spec.Clips)
        {
            foreach (StageSpec stage in clip.Stages)
            {
                if (!string.IsNullOrWhiteSpace(stage.Model) && CanResolveStageVideoModel(g, stage.Model))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool CanResolveStageVideoModel(WorkflowGenerator g, string modelName)
    {
        g.UserInput.Set(T2IParamTypes.VideoModel.Type, modelName);
        bool resolved = g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel m) && m is not null;
        g.UserInput.Remove(T2IParamTypes.VideoModel);
        return resolved;
    }

    public static bool IsRefineSourceVideoMode(WorkflowGenerator g) =>
        g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image source) && source is not null;

    public static int ResolveRefineSkipStages(WorkflowGenerator g, bool refineMode)
    {
        if (!refineMode)
        {
            return 0;
        }
        return g.UserInput.TryGet(VideoStagesExtension.RefineSkipStages, out int value) ? value : 1;
    }
}
