using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using VideoStages.Generated;

namespace VideoStages.Execution.Audio;

/// <summary>Builds audio latents that preserve only authored time windows.</summary>
internal static class AudioPreserveWindowBuilder
{
    internal static WGNodeData TryBuild(
        WorkflowGenerator g,
        WGNodeData audio,
        IReadOnlyList<(double Start, double End)> preserveWindows,
        int stableIdSlot)
    {
        if (audio is null
            || preserveWindows is not { Count: > 0 }
            || g.CurrentAudioVae is null)
        {
            return null;
        }

        WGNodeData encodedAudio = audio.EncodeToLatent(g.CurrentAudioVae);
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        SwarmSetAudioMaskWindowsNode mask = AddMask(
            g,
            bridge,
            encodedAudio.Path,
            preserveWindows,
            stableIdSlot);
        return new WGNodeData(
            WorkflowBridge.ToPath(mask.Latent),
            g,
            WGNodeData.DT_LATENT_AUDIO,
            g.CurrentAudioVae.Compat);
    }

    internal static SwarmSetAudioMaskWindowsNode AddMask(
        WorkflowGenerator g,
        WorkflowBridge bridge,
        JArray encodedAudioPath,
        IReadOnlyList<(double Start, double End)> preserveWindows,
        int stableIdSlot,
        JArray targetAudioPath = null,
        double sourceStartSeconds = 0)
    {
        JArray windowsJson = new(preserveWindows.Select(window => new JObject
        {
            ["start"] = RoundWindowSeconds(window.Start),
            ["end"] = RoundWindowSeconds(window.End),
        }));
        SwarmSetAudioMaskWindowsNode node = new SwarmSetAudioMaskWindowsNode().With(
            Windows: windowsJson.ToString(Newtonsoft.Json.Formatting.None),
            GapMaskValue: 1.0,
            SourceStartSeconds: sourceStartSeconds);
        node.Samples.TryConnectFromPath(bridge, encodedAudioPath);
        node.AudioVae.ConnectFromPath(bridge, g.CurrentAudioVae.Path);
        if (targetAudioPath is not null)
        {
            node.TargetSamples.TryConnectFromPath(bridge, targetAudioPath);
        }
        bridge.AddNode(
            node,
            StableNodeIds.Id(g, StableNodeIds.AudioInjection, 400 + stableIdSlot));
        return node;
    }

    private static double RoundWindowSeconds(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
