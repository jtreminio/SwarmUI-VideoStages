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
/// Owns case-insensitive JSON value access, invariant scalar conversion, and parse diagnostics for
/// the Video Stages document.
/// </summary>
internal static class VideoStagesJsonReader
{
    public static VideoStagesJsonDocument ReadDocument(WorkflowGenerator g)
    {
        string json = VideoStagesPromptSection.IsActive(g) ? VideoStagesPromptSection.GetDataJson(g) : null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VideoStagesJsonDocument(null, null, null, [], []);
        }

        try
        {
            JToken token = JToken.Parse(json);
            if (token is JArray array)
            {
                return new VideoStagesJsonDocument(null, null, null, [.. array.OfType<JObject>()], []);
            }
            if (token is JObject obj)
            {
                return new VideoStagesJsonDocument(
                    GetOptionalNullableInt(obj, "Width"),
                    GetOptionalNullableInt(obj, "Height"),
                    GetOptionalNullableInt(obj, "FPS"),
                    GetObjectArray(obj, "Clips"),
                    GetObjectArray(obj, "AudioTracks"));
            }
            return new VideoStagesJsonDocument(null, null, null, [], []);
        }
        catch (JsonException ex)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: Could not parse Video Stages JSON. {ex.Message}");
        }
    }

    public static string GetString(JObject obj, string key)
    {
        JToken token = JsonUtil.Get(obj, key);
        return token is null || token.Type == JTokenType.Null ? null : $"{token}";
    }

    public static bool GetOptionalBool(JObject obj, string key, bool defaultValue)
    {
        string raw = GetString(obj, key);
        return string.IsNullOrWhiteSpace(raw)
            ? defaultValue
            : bool.TryParse(raw.Trim(), out bool value) ? value : defaultValue;
    }

    public static int GetOptionalInt(JObject obj, string key, int defaultValue, string location)
    {
        JToken token = JsonUtil.Get(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return defaultValue;
        }
        if (token.Type == JTokenType.Integer)
        {
            return token.Value<int>();
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

        Logs.Warning(
            $"VideoStages: {location} has invalid integer field '{key}' value '{raw}'. "
            + $"Using default '{defaultValue}'.");
        return defaultValue;
    }

    public static double GetOptionalDouble(JObject obj, string key, double defaultValue, string location)
    {
        JToken token = JsonUtil.Get(obj, key);
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

        Logs.Warning(
            $"VideoStages: {location} has invalid numeric field '{key}' value '{raw}'. "
            + $"Using default '{defaultValue}'.");
        return defaultValue;
    }

    public static string GetOptionalString(
        JObject obj,
        string key,
        string defaultValue,
        string location,
        bool allowEmpty)
    {
        string value = GetString(obj, key);
        if (value is null)
        {
            return defaultValue;
        }

        value = value.Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            Logs.Warning($"VideoStages: {location} has empty field '{key}'. Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    public static List<JObject> GetObjectArray(JObject obj, string key) =>
        JsonUtil.Get(obj, key) is JArray array ? [.. array.OfType<JObject>()] : [];

    public static JObject GetObject(JObject obj, string key) =>
        JsonUtil.Get(obj, key) as JObject;

    public static bool HasProperty(JObject obj, string key) =>
        JsonUtil.Get(obj, key) is not null;

    public static UploadedMediaSpec GetEmbeddedUpload(JObject parent, string containerPropertyName)
    {
        JObject nested = GetObject(parent, containerPropertyName);
        string data = nested is null ? null : GetString(nested, "Data");
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }
        return new UploadedMediaSpec(data.Trim(), GetString(nested, "FileName")?.Trim());
    }

    private static int? GetOptionalNullableInt(JObject obj, string key)
    {
        JToken token = JsonUtil.Get(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token.Type == JTokenType.Integer)
        {
            return token.Value<int>();
        }

        string raw = $"{token}";
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : null;
    }
}
