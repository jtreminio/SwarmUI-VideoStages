using Newtonsoft.Json.Linq;

namespace VideoStages;

internal static class PathUtils
{
    /// <summary>Clones a 2-element [nodeId, slot] connection path; returns null when the input is null.</summary>
    public static JArray Clone(JArray path) =>
        path is null ? null : new JArray(path[0], path[1]);
}
