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
/// The shared contract fixture checks document keys against the frontend codec.
/// </summary>
internal static class DocumentJson
{
    /// <summary>Must match the frontend's <c>CURRENT_AUTHORING_SCHEMA_VERSION</c>.</summary>
    public const int SupportedSchemaVersion = 11;

    private const int FrameRefsSchemaVersion = 7;
    private const int StageLoraSchemaVersion = 8;
    private const int ClipLoraSchemaVersion = 9;
    private const int ClipAttentionWindowSchemaVersion = 10;

    // The contract test observes all key lookups through Read.
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
            if (schemaVersion == FrameRefsSchemaVersion)
            {
                foreach (JObject entry in entries)
                {
                    RenameFrameRefKeys(entry);
                }
            }
            if (schemaVersion <= StageLoraSchemaVersion)
            {
                foreach (JObject entry in entries)
                {
                    MoveStageLorasToClip(entry);
                }
            }
            if (schemaVersion <= ClipLoraSchemaVersion)
            {
                foreach (JObject entry in entries)
                {
                    JArray loras = entry["loras"] as JArray ?? [];
                    foreach (JObject stage in GetObjectArray(entry, "stages"))
                    {
                        stage["loras"] = loras.DeepClone();
                    }
                    entry.Remove("loras");
                }
            }
            if (schemaVersion <= ClipAttentionWindowSchemaVersion)
            {
                foreach (JObject entry in entries)
                {
                    foreach (JObject stage in GetObjectArray(entry, "stages"))
                    {
                        stage["h3AttentionWindowSeconds"] =
                            entry["h3AttentionWindowSeconds"]?.DeepClone() ?? new JValue(0);
                    }
                    entry.Remove("h3AttentionWindowSeconds");
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

    private static void RenameFrameRefKeys(JObject clip)
    {
        RenameKey(
            clip,
            UploadContainers.VersionSevenKeyframesCollection,
            UploadContainers.KeyframesCollection);
        foreach (JObject stage in GetObjectArray(clip, "stages"))
        {
            RenameKey(stage, "frameRefStrengths", "keyframeStrengths");
        }
    }

    private static void MoveStageLorasToClip(JObject clip)
    {
        JArray clipLoras = clip["loras"] as JArray ?? [];
        List<JObject> stages = GetObjectArray(clip, "stages");
        JArray firstWeights = stages.FirstOrDefault()?["loraWeights"] as JArray;
        HashSet<string> names = [];
        for (int index = 0; index < clipLoras.Count; index++)
        {
            if (clipLoras[index] is not JObject lora)
            {
                continue;
            }
            string name = GetString(lora, "name")?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
            if (!lora.ContainsKey("weight"))
            {
                lora["weight"] = firstWeights is not null && index < firstWeights.Count
                    ? firstWeights[index]
                    : 1;
            }
        }
        foreach (JObject stage in stages)
        {
            foreach (JObject lora in GetObjectArray(stage, "loras"))
            {
                string name = GetString(lora, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                {
                    continue;
                }
                clipLoras.Add(lora.DeepClone());
            }
            stage.Remove("loras");
            stage.Remove("loraWeights");
        }
        clip["loras"] = clipLoras;
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
        if (version is not SupportedSchemaVersion
            and not ClipAttentionWindowSchemaVersion
            and not ClipLoraSchemaVersion
            and not StageLoraSchemaVersion
            and not FrameRefsSchemaVersion)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: The Video Stages timeline uses document version "
                + $"'{(object)version ?? "none"}', but this build only supports version "
                + $"{SupportedSchemaVersion}. Re-save the timeline in the current UI.");
        }
        return version.Value;
    }


    public static JToken GetToken(JObject obj, string key) => Read(obj, key);


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
