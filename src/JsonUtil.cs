using Newtonsoft.Json.Linq;

namespace VideoStages;

/// <summary>Exact-match JObject property access, shared by the spec parser and metadata sanitizer.
/// The Video Stages document is camelCase end to end: the frontend authoring codec emits exactly the
/// keys these readers name, and <c>Tests/fixtures/authoring-document.json</c> pins that pairing.</summary>
internal static class JsonUtil
{
    /// <summary>The value of property <paramref name="key"/>, or null when absent.</summary>
    public static JToken Get(JObject obj, string key) =>
        obj is not null && obj.TryGetValue(key, out JToken token) ? token : null;
}
