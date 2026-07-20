using Newtonsoft.Json.Linq;

namespace VideoStages;

/// <summary>Case-insensitive JObject property access, shared by the spec parser and
/// metadata sanitizer (VideoStages JSON accepts camelCase and PascalCase keys).</summary>
internal static class JsonUtil
{
    /// <summary>The value of the first property matching <paramref name="key"/>
    /// case-insensitively, or null when absent.</summary>
    public static JToken Get(JObject obj, string key) =>
        obj is not null && obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out JToken token)
            ? token
            : null;

    /// <summary>Removes every property matching <paramref name="key"/> case-insensitively.</summary>
    public static void RemoveAll(JObject obj, string key)
    {
        while (obj?.Property(key, StringComparison.OrdinalIgnoreCase) is JProperty property)
        {
            property.Remove();
        }
    }
}
