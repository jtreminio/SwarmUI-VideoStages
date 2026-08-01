using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages;

internal sealed record VideoStagesJsonDocument(
    int? Width,
    int? Height,
    int? Fps,
    List<JObject> Entries,
    List<JObject> AudioTracks);

/// <summary>
/// Owns JSON value access, invariant scalar conversion, and parse diagnostics for the Video Stages
/// document. Every key named here is a camelCase key the frontend authoring codec emits; the shared
/// <c>Tests/fixtures/authoring-document.json</c> contract fixture asserts that pairing.
/// </summary>
internal static class VideoStagesJsonReader
{
    /// <summary>The single authoring document schema version this build parses. Must match the
    /// frontend's <c>CURRENT_AUTHORING_SCHEMA_VERSION</c>.</summary>
    public const int SupportedSchemaVersion = 6;

    private const int ArchitectureHintLegacySchemaVersion = 5;

    /// <summary>Test-only observer of every document key lookup the parser performs, used by the
    /// contract fixture test to prove no reader names a key the frontend never emits.</summary>
    internal static Action<JObject, string, bool> KeyProbe;

    public static VideoStagesJsonDocument ReadDocument(WorkflowGenerator g)
    {
        string json = VideoStagesPromptSection.IsActive(g) ? VideoStagesPromptSection.GetDataJson(g) : null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VideoStagesJsonDocument(null, null, null, [], []);
        }

        try
        {
            if (JToken.Parse(json) is not JObject obj)
            {
                throw new SwarmUserErrorException(
                    "VideoStages: The Video Stages document must be a JSON object.");
            }
            int schemaVersion = ValidateSchemaVersion(obj);
            List<JObject> entries = GetObjectArray(obj, "clips");
            if (schemaVersion == ArchitectureHintLegacySchemaVersion)
            {
                foreach (JObject entry in entries)
                {
                    if (!entry.ContainsKey("architectureHint"))
                    {
                        entry["architectureHint"] = entry["architecture"];
                    }
                    entry.Remove("architecture");
                }
            }
            return new VideoStagesJsonDocument(
                GetOptionalNullableInt(obj, "width"),
                GetOptionalNullableInt(obj, "height"),
                GetOptionalNullableInt(obj, "fps"),
                entries,
                GetObjectArray(obj, "audioTracks"));
        }
        catch (JsonException ex)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: Could not parse Video Stages JSON. {ex.Message}");
        }
    }

    private static int ValidateSchemaVersion(JObject obj)
    {
        int? version = GetOptionalNullableInt(obj, "schemaVersion");
        if (version is not SupportedSchemaVersion and not ArchitectureHintLegacySchemaVersion)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: The Video Stages timeline uses document version "
                + $"'{(object)version ?? "none"}', but this build only supports version "
                + $"{SupportedSchemaVersion}. Re-save the timeline in the current UI.");
        }
        return version.Value;
    }

    /// <summary>The raw value of <paramref name="key"/>, for readers that must distinguish an
    /// explicit null from a wrong-typed value.</summary>
    public static JToken GetToken(JObject obj, string key) => Read(obj, key);

    /// <summary>The single point every document key lookup passes through, so the shared contract
    /// fixture test can observe exactly which keys the backend reads.</summary>
    private static JToken Read(JObject obj, string key)
    {
        JToken token = obj?[key];
        KeyProbe?.Invoke(obj, key, token is not null);
        return token;
    }

    public static string GetString(JObject obj, string key)
    {
        JToken token = Read(obj, key);
        return token is null || token.Type == JTokenType.Null ? null : $"{token}";
    }

    public static bool GetOptionalBool(JObject obj, string key, bool defaultValue)
    {
        string raw = GetString(obj, key);
        return string.IsNullOrWhiteSpace(raw)
            ? defaultValue
            : bool.TryParse(raw.Trim(), out bool value) ? value : defaultValue;
    }

    public static int GetOptionalInt(
        JObject obj,
        string key,
        int defaultValue,
        string location,
        Action<string> warn = null)
    {
        JToken token = Read(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return defaultValue;
        }
        string raw = $"{token}";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        Warn(
            warn,
            $"VideoStages: {location} has invalid integer field '{key}' value '{raw}'. "
            + $"Using default '{defaultValue}'.");
        return defaultValue;
    }

    public static double GetOptionalDouble(
        JObject obj,
        string key,
        double defaultValue,
        string location,
        Action<string> warn = null)
    {
        JToken token = Read(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return defaultValue;
        }
        if (token.Type is JTokenType.Float or JTokenType.Integer)
        {
            return token.Value<double>();
        }

        string raw = $"{token}";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        Warn(
            warn,
            $"VideoStages: {location} has invalid numeric field '{key}' value '{raw}'. "
            + $"Using default '{defaultValue}'.");
        return defaultValue;
    }

    public static string GetOptionalString(
        JObject obj,
        string key,
        string defaultValue,
        string location,
        bool allowEmpty,
        Action<string> warn = null)
    {
        string value = GetString(obj, key);
        if (value is null)
        {
            return defaultValue;
        }

        value = value.Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            Warn(
                warn,
                $"VideoStages: {location} has empty field '{key}'. Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    public static List<JObject> GetObjectArray(JObject obj, string key) =>
        GetArray(obj, key) is { } array ? [.. array.OfType<JObject>()] : [];

    public static JArray GetArray(JObject obj, string key) =>
        Read(obj, key) as JArray;

    public static JObject GetObject(JObject obj, string key) =>
        Read(obj, key) as JObject;

    public static bool HasProperty(JObject obj, string key) =>
        Read(obj, key) is not null;

    public static UploadedMediaSpec GetEmbeddedUpload(JObject parent, string containerPropertyName)
    {
        JObject nested = GetObject(parent, containerPropertyName);
        string data = nested is null ? null : GetString(nested, "data");
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }
        return new UploadedMediaSpec(data.Trim(), GetString(nested, "fileName")?.Trim());
    }

    private static int? GetOptionalNullableInt(JObject obj, string key)
    {
        JToken token = Read(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        string raw = $"{token}";
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : null;
    }

    internal static void Warn(Action<string> warn, string message)
    {
        if (warn is null)
        {
            Logs.Warning(message);
            return;
        }
        warn(message);
    }
}
