using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Builds masked LTX audio latents for authored windows and boundary carry.</summary>
internal static class LtxAudioPreserveWindowBuilder
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

    internal static WGNodeData TryBuildCarry(
        WorkflowGenerator g,
        LtxBoundaryAudioCarry carry,
        int targetFrames,
        int frameRate,
        int stableIdSlot)
    {
        if (carry?.NativeLatent?.Path is not JArray sourcePath
            || g.CurrentAudioVae is null
            || targetFrames <= 0
            || frameRate <= 0)
        {
            return null;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXVEmptyLatentAudioNode target = bridge.AddNode(
            new LTXVEmptyLatentAudioNode().With(
                FramesNumber: targetFrames,
                FrameRate: $"{frameRate}",
                BatchSize: 1));
        target.AudioVae.ConnectFromPath(bridge, g.CurrentAudioVae.Path);
        SwarmSetAudioMaskWindowsNode mask = AddMask(
            g,
            bridge,
            sourcePath,
            [(0, carry.DurationSeconds)],
            stableIdSlot,
            target.Latent.ToPath(),
            carry.SourceStartSeconds);
        return new WGNodeData(
            mask.Latent.ToPath(),
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
        bridge.AddNode(node, StableNodeIds.Id(g, StableNodeIds.AudioInjection, 400 + stableIdSlot));
        return node;
    }

    private static double RoundWindowSeconds(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
