using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

/// <summary>
/// Captures each active core video ControlNet's host image, apply-node and audio facts without
/// applying architecture policy.
/// </summary>
internal sealed class ControlNetCoreMediaCapture(WorkflowGenerator g)
{
    private const string CapturedMarkerKey = "videostages.controlnet.captured";

    public void Capture()
    {
        // Common orchestration invokes this before architecture phase fan-out. Retain the marker
        // as an idempotence guard because host phases may be dispatched more than once in tests or
        // by a future workflow composition.
        if (!g.NodeHelpers.TryAdd(CapturedMarkerKey, "captured"))
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        ControlNetGraphDiscovery discovery = new(g);
        HashSet<string> usedApplyNodes = [];
        for (int index = 0; index < T2IParamTypes.Controlnets.Length; index++)
        {
            T2IParamTypes.ControlNetParamHolder parameters = T2IParamTypes.Controlnets[index];
            if (parameters is null
                || !g.UserInput.TryGet(parameters.Strength, out double _)
                || !g.UserInput.TryGet(parameters.Model, out T2IModel model)
                || !discovery.TryFindCoreApply(
                    bridge, model, usedApplyNodes, out (string Id, JObject Node) applyNode, out JArray controlImage)
                || !HasVideoUpstream(bridge, controlImage))
            {
                VideoGraphHelpers.RemoveCached(g, ControlNetCaptureKeys.Image(index));
                VideoGraphHelpers.RemoveCached(g, ControlNetCaptureKeys.Audio(index));
                VideoGraphHelpers.RemoveCached(g, ControlNetCaptureKeys.Apply(index));
                continue;
            }

            VideoGraphHelpers.CachePath(
                g,
                ControlNetCaptureKeys.Image(index),
                new JArray(controlImage[0], controlImage[1]));
            g.NodeHelpers[ControlNetCaptureKeys.Apply(index)] = applyNode.Id;
            ControlNetAudioCapture.CaptureUpstreamAudio(g, bridge, controlImage, index);
            usedApplyNodes.Add(applyNode.Id);
        }
    }

    /// <summary>Resolves the cached capture against the live graph: a captured node that a later
    /// cleanup removed must read as "not captured", never as a dangling reference.</summary>
    internal static bool TryGetCapturedControlImage(
        WorkflowGenerator g,
        int index,
        out WGNodeData controlImage)
    {
        controlImage = null;
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (!ControlNetCaptureKeys.IsValidIndex(index)
            || !VideoGraphHelpers.TryGetCachedPath(
                g, bridge, ControlNetCaptureKeys.Image(index), out JArray path))
        {
            return false;
        }
        controlImage = new WGNodeData(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        return true;
    }

    internal static bool HasVideoUpstream(WorkflowBridge bridge, JArray outputRef)
    {
        if (outputRef is not { Count: 2 } || bridge.NodeAt(outputRef) is not ComfyNode start)
        {
            return false;
        }
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [start.Id];
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is SwarmLoadVideoB64Node or GetVideoComponentsNode)
            {
                return true;
            }
            // FindUpstream, not Inputs: it also reads autogrow list children, so a batch node
            // between here and the loaded footage does not hide it.
            foreach (ComfyNode upstream in bridge.Graph.FindUpstream(current))
            {
                if (visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }
        return false;
    }
}
