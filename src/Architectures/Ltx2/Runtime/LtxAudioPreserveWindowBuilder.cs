using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Encodes authored audio and masks only its occupied timeline windows as preserved. Keeping this
/// independent of root-concat replacement lets clips defer the work until their audio VAE exists.
/// </summary>
internal static class LtxAudioPreserveWindowBuilder
{
    private const int AudioInjectionIdBase = 52300;

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
        int stableIdSlot)
    {
        JArray windowsJson = new(preserveWindows.Select(window => new JObject
        {
            ["start"] = RoundWindowSeconds(window.Start),
            ["end"] = RoundWindowSeconds(window.End),
        }));
        SwarmSetAudioMaskWindowsNode node = new SwarmSetAudioMaskWindowsNode().With(
            Windows: windowsJson.ToString(Newtonsoft.Json.Formatting.None),
            GapMaskValue: 1.0);
        node.Samples.TryConnectFromPath(bridge, encodedAudioPath);
        node.AudioVae.ConnectFromPath(bridge, g.CurrentAudioVae.Path);
        bridge.AddNode(node, g.GetStableDynamicID(AudioInjectionIdBase + 400, stableIdSlot));
        return node;
    }

    private static double RoundWindowSeconds(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
