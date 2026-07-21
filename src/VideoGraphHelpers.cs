using ComfyTyped.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal static class VideoGraphHelpers
{
    /// <summary>Reads a bridge-resolvable [nodeId, slot] path cached in NodeHelpers
    /// under <paramref name="key"/>; false when absent or no longer resolvable.</summary>
    public static bool TryGetCachedPath(WorkflowGenerator g, WorkflowBridge bridge, string key, out JArray path)
    {
        if (g.NodeHelpers.TryGetValue(key, out string encoded)
            && !string.IsNullOrWhiteSpace(encoded)
            && JToken.Parse(encoded) is JArray { Count: 2 } cached
            && (bridge is null || bridge.ResolvePath(cached) is not null))
        {
            path = cached;
            return true;
        }
        path = null;
        return false;
    }

    public static void CachePath(WorkflowGenerator g, string key, JArray path) =>
        g.NodeHelpers[key] = path.ToString(Formatting.None);

    public static bool IsImageDataUri(string data) =>
        data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    public static string StripDataUriPrefix(string data)
    {
        int comma = data.IndexOf(',');
        return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
            ? data[(comma + 1)..]
            : data;
    }

    public static bool TryGetInputRef(JObject node, string inputName, out JArray inputRef)
    {
        inputRef = null;
        if (node["inputs"] is not JObject inputs
            || !inputs.TryGetValue(inputName, out JToken token)
            || token is not JArray { Count: 2 } array)
        {
            return false;
        }
        inputRef = array;
        return true;
    }
}
