using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Builds the LTX-specific latent used for cross-clip audio carry.</summary>
internal static class LtxAudioPreserveWindowBuilder
{
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
        SwarmSetAudioMaskWindowsNode mask = AudioPreserveWindowBuilder.AddMask(
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

}
