using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages;

public class StageRefStore(WorkflowGenerator g)
{
    private const string Prefix = "videostages.";

    public enum StageKind
    {
        Base,
        Refiner,
        Generated,
        PreRootVideo
    }

    public sealed record StageRef(
        WGNodeData Media,
        WGNodeData Vae
    );

    private static string StageName(StageKind kind) => kind switch
    {
        StageKind.Base => "base",
        StageKind.Refiner => "refiner",
        StageKind.Generated => "generated",
        StageKind.PreRootVideo => "preroot",
        _ => throw new SwarmReadableErrorException($"Unhandled StageKind value: {kind}")
    };

    private static string MediaKey(StageKind kind) => $"{Prefix}{StageName(kind)}.media";
    private static string VaeKey(StageKind kind) => $"{Prefix}{StageName(kind)}.vae";
    private static string AudioKey(StageKind kind) => $"{Prefix}{StageName(kind)}.media.audio";

    public StageRef Base => GetIfCaptured(StageKind.Base);

    public StageRef Refiner => GetIfCaptured(StageKind.Refiner);

    public StageRef Generated => GetIfCaptured(StageKind.Generated);

    public StageRef PreRootVideo => GetIfCaptured(StageKind.PreRootVideo);

    public bool DiscardPreRootVideo()
    {
        bool removedMedia = g.NodeHelpers.Remove(MediaKey(StageKind.PreRootVideo));
        bool removedVae = g.NodeHelpers.Remove(VaeKey(StageKind.PreRootVideo));
        g.NodeHelpers.Remove(AudioKey(StageKind.PreRootVideo));
        return removedMedia || removedVae;
    }

    public void Capture(
        StageKind kind,
        WGNodeData mediaOverride = null,
        WGNodeData vaeOverride = null)
    {
        WGNodeData media = mediaOverride ?? g.CurrentMedia;
        WGNodeData vae = vaeOverride ?? g.CurrentVae;
        StoreMarker(MediaKey(kind), media);
        StoreMarker(VaeKey(kind), vae);
        if (media?.AttachedAudio is not null)
        {
            StoreMarker(AudioKey(kind), media.AttachedAudio);
        }
        else
        {
            g.NodeHelpers.Remove(AudioKey(kind));
        }
    }

    private StageRef GetIfCaptured(StageKind kind)
    {
        return g.NodeHelpers.ContainsKey(MediaKey(kind)) ? LoadStageRef(kind) : null;
    }

    private void StoreMarker(string key, WGNodeData data)
    {
        if (data?.Path is not JArray { Count: 2 } path)
        {
            g.NodeHelpers.Remove(key);
            return;
        }
        g.NodeHelpers[key] = string.Join("|",
            $"{path[0]}", $"{path[1]}",
            data.DataType ?? WGNodeData.DT_IMAGE,
            data.Width.HasValue ? $"{data.Width.Value}" : "",
            data.Height.HasValue ? $"{data.Height.Value}" : "",
            data.Frames.HasValue ? $"{data.Frames.Value}" : "",
            data.GetRawFPS() is int fpsVal ? $"{fpsVal}" : "",
            data.Compat?.ID ?? "");
    }

    private StageRef LoadStageRef(StageKind kind)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        WGNodeData vae = LoadMarker(bridge, VaeKey(kind), fallbackVae: null);
        WGNodeData media = LoadMarker(bridge, MediaKey(kind), fallbackVae: vae);
        if (media is not null)
        {
            media.AttachedAudio = LoadMarker(bridge, AudioKey(kind), fallbackVae: g.CurrentAudioVae);
        }
        return new StageRef(Media: media, Vae: vae);
    }

    private WGNodeData LoadMarker(WorkflowBridge bridge, string key, WGNodeData fallbackVae)
    {
        if (!g.NodeHelpers.TryGetValue(key, out string encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }
        string[] parts = encoded.Split('|');
        if (parts.Length < 8 || !int.TryParse(parts[1], out int slot))
        {
            return null;
        }
        string nodeId = parts[0];
        ComfyNode node = bridge.Graph.GetNode(nodeId);
        if (node is null)
        {
            Logs.Warning($"VideoStages: node '{nodeId}' not found in workflow; treating as not captured.");
            return null;
        }
        INodeOutput output = node.FindOutput(slot);
        if (output is null)
        {
            Logs.Warning($"VideoStages: slot {slot} on node '{nodeId}' not found; treating as not captured.");
            return null;
        }
        return WGNodeDataMarkerCodec.Build(
            g, output, parts[2], parts[7], fallbackVae,
            Nullable(parts[3]), Nullable(parts[4]), Nullable(parts[5]), Nullable(parts[6]));
    }

    private static int? Nullable(string s) =>
        !string.IsNullOrEmpty(s) && int.TryParse(s, out int v) ? v : null;
}
