using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

public class StageRefStore(WorkflowGenerator g)
{
    private const string Prefix = "videostages.";

    public enum StageKind
    {
        Base,
        Refiner,
        Generated,
        Stage
    }

    public sealed record StageRef(
        WGNodeData Media,
        WGNodeData Vae
    );

    private static string StageName(StageKind kind, int? index) => kind switch
    {
        StageKind.Base => "base",
        StageKind.Refiner => "refiner",
        StageKind.Generated => "generated",
        StageKind.Stage => $"stage.{index ?? 0}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string NodeKey(StageKind kind, int? index, string property) => $"{Prefix}{StageName(kind, index)}.{property}";

    public StageRef Base => GetIfCaptured(StageKind.Base);

    public StageRef Refiner => GetIfCaptured(StageKind.Refiner);

    public StageRef Generated => GetIfCaptured(StageKind.Generated);

    public void Capture(StageKind kind, int? index = null, WGNodeData mediaOverride = null, WGNodeData vaeOverride = null)
    {
        StoreNodeData(NodeKey(kind, index, "media"), mediaOverride ?? g.CurrentMedia);
        StoreNodeData(NodeKey(kind, index, "vae"), vaeOverride ?? g.CurrentVae);
    }

    public bool TryGetStageRef(int index, out StageRef stageRef)
    {
        stageRef = null;
        if (!HasCaptured(StageKind.Stage, index))
        {
            return false;
        }

        stageRef = LoadStageRef(StageKind.Stage, index);
        return true;
    }

    private StageRef GetIfCaptured(StageKind kind) => HasCaptured(kind) ? LoadStageRef(kind) : null;

    private bool HasCaptured(StageKind kind, int? index = null) => g.NodeHelpers.ContainsKey(NodeKey(kind, index, "media"));

    private void StoreNodeData(string key, WGNodeData data)
    {
        if (data?.Path is not JArray path || path.Count != 2)
        {
            g.NodeHelpers.Remove(key);
            return;
        }

        JObject encoded = SerializeNodeData(data);
        g.NodeHelpers[key] = encoded.ToString(Formatting.None);
    }

    private StageRef LoadStageRef(StageKind kind, int? index = null)
    {
        WGNodeData vae = LoadNodeData(NodeKey(kind, index, "vae"), fallbackVae: null);
        return new StageRef(
            Media: LoadNodeData(NodeKey(kind, index, "media"), vae),
            Vae: vae
        );
    }

    private WGNodeData LoadNodeData(string key, WGNodeData fallbackVae)
    {
        if (!g.NodeHelpers.TryGetValue(key, out string encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            return JToken.Parse(encoded) is JObject obj
                ? DeserializeNodeData(obj, fallbackVae)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private WGNodeData DeserializeNodeData(JObject data, WGNodeData fallbackVae)
    {
        if (data["path"] is not JArray path || path.Count != 2)
        {
            return null;
        }

        string dataType = data.Value<string>("dataType") ?? WGNodeData.DT_IMAGE;
        T2IModelCompatClass compat = ResolveCompatFor(dataType, fallbackVae, data.Value<string>("compatId"));
        WGNodeData restored = new(path, g, dataType, compat)
        {
            Width = data.Value<int?>("width"),
            Height = data.Value<int?>("height"),
            Frames = data.Value<int?>("frames"),
            FPS = data.Value<int?>("fps")
        };

        if (data.TryGetValue("attachedAudio", out JToken attachedAudio) && attachedAudio is JObject audioObj)
        {
            restored.AttachedAudio = DeserializeNodeData(audioObj, g.CurrentAudioVae);
        }
        return restored;
    }

    private T2IModelCompatClass ResolveCompatFor(string dataType, WGNodeData fallbackVae, string compatId)
    {
        if (!string.IsNullOrWhiteSpace(compatId)
            && T2IModelClassSorter.CompatClasses.TryGetValue(compatId.ToLowerFast(), out T2IModelCompatClass explicitCompat))
        {
            return explicitCompat;
        }
        if (dataType == WGNodeData.DT_AUDIO || dataType == WGNodeData.DT_LATENT_AUDIO || dataType == WGNodeData.DT_AUDIOVAE)
        {
            return g.CurrentAudioVae?.Compat;
        }
        if (dataType == WGNodeData.DT_VAE && g.CurrentVae is not null)
        {
            return g.CurrentVae.Compat;
        }
        return fallbackVae?.Compat ?? g.CurrentVae?.Compat ?? g.CurrentCompat();
    }

    private static JObject SerializeNodeData(WGNodeData data)
    {
        JObject result = new()
        {
            ["path"] = new JArray(data.Path[0], data.Path[1]),
            ["dataType"] = data.DataType
        };

        if (!string.IsNullOrWhiteSpace(data.Compat?.ID))
        {
            result["compatId"] = data.Compat.ID;
        }

        AddIfHasValue(result, "width", data.Width);
        AddIfHasValue(result, "height", data.Height);
        AddIfHasValue(result, "frames", data.Frames);
        AddIfHasValue(result, "fps", data.FPS);
        if (data.AttachedAudio is not null)
        {
            result["attachedAudio"] = SerializeNodeData(data.AttachedAudio);
        }
        return result;
    }

    private static void AddIfHasValue(JObject o, string key, int? value)
    {
        if (value.HasValue)
        {
            o[key] = value.Value;
        }
    }
}
