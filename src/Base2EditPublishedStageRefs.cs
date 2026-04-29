using System.Diagnostics.CodeAnalysis;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class Base2EditPublishedStageRefs(WorkflowGenerator g)
{
    private const string Prefix = "b2e.published.edit.";

    public bool TryGetStageRef(
        int stageIndex,
        [MaybeNullWhen(false)] out StageRefStore.StageRef stageRef)
    {
        stageRef = null;
        if (!g.NodeHelpers.TryGetValue($"{Prefix}{stageIndex}", out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            JObject payload = JObject.Parse(encoded);
            WGNodeData vae = payload["vae"] is JObject vaeObj ? DeserializeNodeData(vaeObj, null) : null;
            WGNodeData media = payload["media"] is JObject mediaObj ? DeserializeNodeData(mediaObj, vae) : null;
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

    private WGNodeData DeserializeNodeData(JObject data, WGNodeData fallbackVae)
    {
        if (data["path"] is not JArray path || path.Count != 2)
        {
            return null;
        }

        string dataType = data.Value<string>("dataType") ?? WGNodeData.DT_IMAGE;
        T2IModelCompatClass compat = ResolveCompatFor(dataType, fallbackVae, data.Value<string>("compatId"));
        return new WGNodeData(path, g, dataType, compat)
        {
            Width = data.Value<int?>("width"),
            Height = data.Value<int?>("height"),
            Frames = data.Value<int?>("frames"),
            FPS = data.Value<int?>("fps")
        };
    }

    private T2IModelCompatClass ResolveCompatFor(
        string dataType,
        WGNodeData fallbackVae,
        string compatId)
    {
        if (!string.IsNullOrWhiteSpace(compatId)
            && T2IModelClassSorter.CompatClasses.TryGetValue(
                compatId.ToLowerFast(),
                out T2IModelCompatClass explicitCompat))
        {
            return explicitCompat;
        }
        if (dataType == WGNodeData.DT_VAE && g.CurrentVae is not null)
        {
            return g.CurrentVae.Compat;
        }
        return fallbackVae?.Compat ?? g.CurrentVae?.Compat ?? g.CurrentCompat();
    }
}
