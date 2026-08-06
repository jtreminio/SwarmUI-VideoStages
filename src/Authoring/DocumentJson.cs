using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages.Authoring;

internal sealed record AuthoringDocument(
    int? Width,
    int? Height,
    int? Fps,
    List<JObject> Clips,
    List<JObject> AudioTracks);

/// <summary>
/// Owns the Enabled/Data carrier, schema validation and migration, and typed JSON reads for the
/// authored document. The shared contract fixture checks its keys against the frontend codec.
/// </summary>
internal static class DocumentJson
{
    /// <summary>The current authoring schema version. Reading also accepts the bounded version-6
    /// migration below. Must match the frontend's <c>CURRENT_AUTHORING_SCHEMA_VERSION</c>.</summary>
    public const int SupportedSchemaVersion = 7;

    private const int FrameRefsLegacySchemaVersion = 6;

    /// <summary>Test-only observer of every document key lookup the request reader performs, used by the
    /// contract fixture test to prove no reader names a key the frontend never emits.</summary>
    internal static Action<JObject, string, bool> KeyProbe;

    public static bool IsActive(T2IParamInput input) =>
        input.TryGetRaw(VideoStagesExtension.Enabled.Type, out _)
        && !string.IsNullOrWhiteSpace(GetData(input));

    public static AuthoringDocument Read(T2IParamInput input)
    {
        string json = IsActive(input) ? GetData(input) : null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AuthoringDocument(null, null, null, [], []);
        }

        try
        {
            if (JToken.Parse(json) is not JObject obj)
            {
                throw new SwarmUserErrorException(
                    "VideoStages: The Video Stages document must be a JSON object.");
            }
            int schemaVersion = ValidateSchemaVersion(obj);
            List<JObject> entries = GetObjectArray(obj, UploadContainers.ClipsCollection);
            if (schemaVersion == FrameRefsLegacySchemaVersion)
            {
                foreach (JObject entry in entries)
                {
                    RenameFrameRefKeys(entry);
                }
            }
            return new AuthoringDocument(
                GetOptionalNullableInt(obj, "width"),
                GetOptionalNullableInt(obj, "height"),
                GetOptionalNullableInt(obj, "fps"),
                entries,
                GetObjectArray(obj, UploadContainers.AudioTracksCollection));
        }
        catch (JsonException ex)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: Could not parse Video Stages JSON. {ex.Message}");
        }
    }

    private static string GetData(T2IParamInput input) =>
        input.Get(VideoStagesExtension.Data, "");

    /// <summary>The sole v6-&gt;v7 migration: the frame-reference keys gained their "frame" prefix so
    /// they no longer read as the position-free references other architectures accept.</summary>
    private static void RenameFrameRefKeys(JObject clip)
    {
        RenameKey(clip, "refs", UploadContainers.FrameRefsCollection);
        foreach (JObject stage in GetObjectArray(clip, "stages"))
        {
            RenameKey(stage, "refStrengths", "frameRefStrengths");
        }
    }

    private static void RenameKey(JObject obj, string oldKey, string newKey)
    {
        if (!obj.TryGetValue(oldKey, out JToken value))
        {
            return;
        }
        obj.Remove(oldKey);
        if (!obj.ContainsKey(newKey))
        {
            obj[newKey] = value;
        }
    }

    private static int ValidateSchemaVersion(JObject obj)
    {
        int? version = GetOptionalNullableInt(obj, "schemaVersion");
        if (version is not SupportedSchemaVersion and not FrameRefsLegacySchemaVersion)
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
