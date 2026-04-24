using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal static class Base2EditPublishedStageRefs
{
    private const string Prefix = "b2e.published.edit.";

    public static bool TryGetStageRef(WorkflowGenerator g, int stageIndex, out StageRefStore.StageRef stageRef)
    {
        stageRef = null;
        if (g is null
            || !g.NodeHelpers.TryGetValue($"{Prefix}{stageIndex}", out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            if (JToken.Parse(encoded) is not JObject payload)
            {
                return false;
            }

            WGNodeData vae = DeserializeNodeData(g, payload["vae"] as JObject, null);
            WGNodeData media = DeserializeNodeData(g, payload["media"] as JObject, vae);
            if (media is null)
            {
                return false;
            }

            stageRef = new StageRefStore.StageRef(media, vae);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static WGNodeData ResolveToRawImage(StageRefStore.StageRef stageRef)
    {
        if (stageRef?.Media is null)
        {
            return null;
        }
        if (stageRef.Media.IsRawMedia)
        {
            return stageRef.Media;
        }
        if (!stageRef.Media.IsLatentData || stageRef.Vae is null)
        {
            return null;
        }

        return VaeDecodePreference.AsRawImage(stageRef.Media.Gen, stageRef.Media, stageRef.Vae);
    }

    private static WGNodeData DeserializeNodeData(WorkflowGenerator g, JObject data, WGNodeData fallbackVae)
    {
        if (data?["path"] is not JArray path || path.Count != 2)
        {
            return null;
        }

        string dataType = data.Value<string>("dataType") ?? WGNodeData.DT_IMAGE;
        T2IModelCompatClass compat = ResolveCompatFor(g, dataType, fallbackVae, data.Value<string>("compatId"));
        WGNodeData restored = new(path, g, dataType, compat)
        {
            Width = data.Value<int?>("width"),
            Height = data.Value<int?>("height"),
            Frames = data.Value<int?>("frames"),
            FPS = data.Value<int?>("fps")
        };
        return restored;
    }

    private static T2IModelCompatClass ResolveCompatFor(WorkflowGenerator g, string dataType, WGNodeData fallbackVae, string compatId)
    {
        if (!string.IsNullOrWhiteSpace(compatId)
            && T2IModelClassSorter.CompatClasses.TryGetValue(compatId.ToLowerFast(), out T2IModelCompatClass explicitCompat))
        {
            return explicitCompat;
        }
        if (dataType == WGNodeData.DT_VAE && g?.CurrentVae is not null)
        {
            return g.CurrentVae.Compat;
        }
        return fallbackVae?.Compat ?? g?.CurrentVae?.Compat ?? g?.CurrentCompat();
    }
}
