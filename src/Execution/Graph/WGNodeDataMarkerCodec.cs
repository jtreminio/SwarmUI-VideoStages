using ComfyTyped.Core;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

/// <summary>Rebuilds <see cref="WGNodeData"/> from stage-reference fields shared by pipe and JSON marker codecs.</summary>
internal static class WGNodeDataMarkerCodec
{
    public static WGNodeData Build(
        WorkflowGenerator g,
        INodeOutput output,
        string dataType,
        string compatId,
        WGNodeData fallbackVae,
        int? width,
        int? height,
        int? frames,
        int? fps)
    {
        dataType = string.IsNullOrEmpty(dataType) ? WGNodeData.DT_IMAGE : dataType;
        T2IModelCompatClass compat = ResolveCompatFor(g, dataType, fallbackVae, compatId);
        return new WGNodeData(WorkflowBridge.ToPath(output), g, dataType, compat)
        {
            Width = width,
            Height = height,
            Frames = frames,
            FPS = fps is int f ? new JValue(f) : null
        };
    }

    public static T2IModelCompatClass ResolveCompatFor(
        WorkflowGenerator g,
        string dataType,
        WGNodeData fallbackVae,
        string compatId)
    {
        if (!string.IsNullOrWhiteSpace(compatId)
            && T2IModelClassSorter.CompatClasses.TryGetValue(compatId.ToLowerFast(), out T2IModelCompatClass c))
        {
            return c;
        }
        if (dataType == WGNodeData.DT_AUDIO || dataType == WGNodeData.DT_LATENT_AUDIO || dataType == WGNodeData.DT_AUDIOVAE)
        {
            return g.CurrentAudioVae?.Compat;
        }
        if (dataType == WGNodeData.DT_VAE && g.CurrentVae is not null)
        {
            return g.CurrentVae.Compat;
        }
        return fallbackVae?.Compat ?? g.CurrentVae?.Compat ?? g.CurrentCompat();
    }
}
