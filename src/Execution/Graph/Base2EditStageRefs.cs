using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Execution.Graph;

internal static class Base2EditStageRefs
{
    private const string PublishedEditPrefix = "b2e.published.edit.";

    internal static bool TryGet(
        WorkflowGenerator g,
        int stageIndex,
        out WGNodeData media,
        out WGNodeData vae)
    {
        media = null;
        vae = null;
        if (!g.NodeHelpers.TryGetValue($"{PublishedEditPrefix}{stageIndex}", out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }
        JObject payload;
        try
        {
            payload = JToken.Parse(encoded) as JObject;
        }
        catch
        {
            return false;
        }
        if (payload?["media"] is not JObject mediaObject)
        {
            return false;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        vae = Build(g, bridge, payload["vae"] as JObject, fallbackVae: null);
        media = Build(g, bridge, mediaObject, fallbackVae: vae);
        return media is not null;
    }

    private static WGNodeData Build(
        WorkflowGenerator g,
        WorkflowBridge bridge,
        JObject data,
        WGNodeData fallbackVae)
    {
        if (data is null
            || data["path"] is not JArray rawPath
            || bridge.ResolvePath(rawPath) is not INodeOutput output)
        {
            return null;
        }
        return WGNodeDataMarkerCodec.Build(
            g,
            output,
            data.Value<string>("dataType"),
            data.Value<string>("compatId"),
            fallbackVae,
            data.Value<int?>("width"),
            data.Value<int?>("height"),
            data.Value<int?>("frames"),
            data.Value<int?>("fps"));
    }
}
