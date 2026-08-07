using System.Diagnostics.CodeAnalysis;
using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;

namespace VideoStages.Architectures.Ltx2;

internal class StageRefStore(WorkflowGenerator g)
{
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
        if (!Base2EditStageRefs.TryGet(g, stageIndex, out WGNodeData media, out WGNodeData vae))
        {
            stageRef = null;
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
    /// The stage output before LTX post-video decode: the joint AV latent when a post-video chain
    /// exists, otherwise the current media.
    /// </summary>
    public StageRef CurrentOutputReference()
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        LtxPostVideoChain postVideoChain = LtxPostVideoChain.TryCapture(g);
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
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: node '{nodeId}' not found in workflow; treating as not captured.");
            return null;
        }
        INodeOutput output = node.FindOutput(slot);
        if (output is null)
        {
            RequestWarnings.Track(
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
