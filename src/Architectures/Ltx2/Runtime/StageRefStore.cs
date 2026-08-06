using System.Diagnostics.CodeAnalysis;
using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal class StageRefStore(WorkflowGenerator g)
{
    private const string Base2EditPrefix = "b2e.published.edit.";

    public enum StageKind
    {
        Base,
        Refiner,
        Generated
    }

    public sealed record StageRef(
        WGNodeData Media,
        WGNodeData Vae
    );

    private string MediaKey(StageKind kind) =>
        LtxRuntimeKeyScope.StageRefMedia(kind);

    private string VaeKey(StageKind kind) =>
        LtxRuntimeKeyScope.StageRefVae(kind);

    private string AudioKey(StageKind kind) =>
        LtxRuntimeKeyScope.StageRefAudio(kind);

    public StageRef Base => GetIfCaptured(StageKind.Base);

    public StageRef Refiner => GetIfCaptured(StageKind.Refiner);

    public StageRef Generated => GetIfCaptured(StageKind.Generated);

    public bool TryGetBase2EditStageRef(
        int stageIndex,
        [MaybeNullWhen(false)] out StageRef stageRef)
    {
        stageRef = null;
        if (!g.NodeHelpers.TryGetValue($"{Base2EditPrefix}{stageIndex}", out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        JObject payload;
        try
        {
            payload = JToken.Parse(encoded) as JObject;
        }
        catch
        {
            return false;
        }
        if (payload?["media"] is not JObject mediaObj)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        WGNodeData vae = BuildNodeData(bridge, payload["vae"] as JObject, fallbackVae: null);
        WGNodeData media = BuildNodeData(bridge, mediaObj, fallbackVae: vae);
        if (media is null)
        {
            return false;
        }

        stageRef = new StageRef(media, vae);
        return true;
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
            VideoGraphHelpers.RemoveCached(g, AudioKey(kind));
        }
    }

    /// <summary>
    /// Returns the decoded stage output before LTX post-video processing.
    /// </summary>
    public StageRef CaptureCurrentOutputReference()
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        LtxPostVideoChainCapture postVideoChain = LtxPostVideoChainCapture.TryCapture(g);
        if (postVideoChain is not null)
        {
            referenceMedia = postVideoChain.CreateStageInput();
            referenceVae = postVideoChain.CreateStageInputVae();
        }
        return new(referenceMedia, referenceVae);
    }

    private StageRef GetIfCaptured(StageKind kind)
    {
        return g.NodeHelpers.ContainsKey(MediaKey(kind)) ? LoadStageRef(kind) : null;
    }

    private void StoreMarker(string key, WGNodeData data)
    {
        if (data?.Path is not JArray { Count: 2 } path)
        {
            VideoGraphHelpers.RemoveCached(g, key);
            return;
        }
        VideoGraphHelpers.CacheMarker(g, key, [
            $"{path[0]}", $"{path[1]}",
            data.DataType ?? WGNodeData.DT_IMAGE,
            data.Width.HasValue ? $"{data.Width.Value}" : "",
            data.Height.HasValue ? $"{data.Height.Value}" : "",
            data.Frames.HasValue ? $"{data.Frames.Value}" : "",
            data.GetRawFPS() is int fpsVal ? $"{fpsVal}" : "",
            data.Compat?.ID ?? ""]);
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

    private WGNodeData BuildNodeData(WorkflowBridge bridge, JObject data, WGNodeData fallbackVae)
    {
        if (data is null
            || data["path"] is not JArray rawPath
            || bridge.ResolvePath(rawPath) is not INodeOutput output)
        {
            return null;
        }

        return WGNodeDataMarkerCodec.Build(
            g,
            output,
            data.Value<string>("dataType"),
            data.Value<string>("compatId"),
            fallbackVae,
            data.Value<int?>("width"),
            data.Value<int?>("height"),
            data.Value<int?>("frames"),
            data.Value<int?>("fps"));
    }

    private WGNodeData LoadMarker(WorkflowBridge bridge, string key, WGNodeData fallbackVae)
    {
        if (!g.NodeHelpers.TryGetValue(key, out string encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }
        string[] parts = encoded.Split(VideoGraphHelpers.MarkerSeparator);
        if (parts.Length < 8 || !int.TryParse(parts[1], out int slot))
        {
            return null;
        }
        string nodeId = parts[0];
        ComfyNode node = bridge.Graph.GetNode(nodeId);
        if (node is null)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: node '{nodeId}' not found in workflow; treating as not captured.");
            return null;
        }
        INodeOutput output = node.FindOutput(slot);
        if (output is null)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: slot {slot} on node '{nodeId}' not found; treating as not captured.");
            return null;
        }
        return WGNodeDataMarkerCodec.Build(
            g, output, parts[2], parts[7], fallbackVae,
            Nullable(parts[3]), Nullable(parts[4]), Nullable(parts[5]), Nullable(parts[6]));
    }

    private static int? Nullable(string s) =>
        !string.IsNullOrEmpty(s) && int.TryParse(s, out int v) ? v : null;
}
