using System.Diagnostics.CodeAnalysis;
using ComfyTyped.Core;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class Base2EditPublishedStageRefs(WorkflowGenerator g)
{
    private const string Prefix = "b2e.published.edit.";

    private sealed class NodeDataPayload
    {
        [JsonProperty("path")] public JArray Path { get; set; }
        [JsonProperty("dataType")] public string DataType { get; set; }
        [JsonProperty("compatId")] public string CompatId { get; set; }
        [JsonProperty("width")] public int? Width { get; set; }
        [JsonProperty("height")] public int? Height { get; set; }
        [JsonProperty("frames")] public int? Frames { get; set; }
        [JsonProperty("fps")] public int? FPS { get; set; }
    }

    private sealed class StagePayload
    {
        [JsonProperty("media")] public NodeDataPayload Media { get; set; }
        [JsonProperty("vae")] public NodeDataPayload Vae { get; set; }
    }

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

        StagePayload payload;
        try
        {
            payload = JsonConvert.DeserializeObject<StagePayload>(encoded);
        }
        catch (JsonException)
        {
            return false;
        }
        if (payload?.Media is null)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        WGNodeData vae = BuildNodeData(bridge, payload.Vae, fallbackVae: null);
        WGNodeData media = BuildNodeData(bridge, payload.Media, fallbackVae: vae);
        if (media is null)
        {
            return false;
        }

        stageRef = new StageRefStore.StageRef(media, vae);
        return true;
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

    private WGNodeData BuildNodeData(
        WorkflowBridge bridge,
        NodeDataPayload data,
        WGNodeData fallbackVae)
    {
        if (data is null || bridge.ResolvePath(data.Path) is not INodeOutput output)
        {
            return null;
        }

        string dataType = data.DataType ?? WGNodeData.DT_IMAGE;
        T2IModelCompatClass compat = ResolveCompatFor(dataType, fallbackVae, data.CompatId);
        return new WGNodeData(WorkflowBridge.ToPath(output), g, dataType, compat)
        {
            Width = data.Width,
            Height = data.Height,
            Frames = data.Frames,
            FPS = data.FPS
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
