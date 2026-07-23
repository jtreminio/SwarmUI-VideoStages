using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

internal static class LtxPostVideoChainSplicer
{
    public static void SpliceCurrentOutput(LtxPostVideoChainCapture capture, WorkflowGenerator g, WGNodeData vae)
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 })
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        LtxChainCapture chainCapture =
            LtxPostVideoChainInspector.Rehydrate(capture.State, bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        MediaRef vaeRef =
            MediaRef.FromWGNodeData(vae, bridge) ?? MediaRef.FromWGNodeData(g.CurrentVae, bridge);
        LtxDecodeConfig decodeConfig = LtxPostChainRebuilder.BuildDecodeConfig(g);

        MediaRef result =
            LtxPostChainRebuilder.SpliceCurrentOutput(
                bridge,
                chainCapture,
                stageOutput,
                vaeRef,
                decodeConfig);

        if (result is not null)
        {
            g.CurrentMedia = result.ToWGNodeData(g);
            capture.AttachSourceAudio(g.CurrentMedia);
        }
    }

    public static void SpliceCurrentOutputToDedicatedBranch(
        LtxPostVideoChainCapture capture,
        WorkflowGenerator g,
        WGNodeData vae,
        int outputWidth,
        int outputHeight,
        int? outputFrames,
        int? outputFps)
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 })
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        LtxChainCapture chainCapture =
            LtxPostVideoChainInspector.Rehydrate(capture.State, bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        MediaRef vaeRef =
            MediaRef.FromWGNodeData(vae, bridge) ?? MediaRef.FromWGNodeData(g.CurrentVae, bridge);
        LtxDecodeConfig decodeConfig = LtxPostChainRebuilder.BuildDecodeConfig(g);

        MediaRef result = LtxPostChainRebuilder.SpliceCurrentOutputToDedicatedBranch(
            bridge,
            chainCapture,
            stageOutput,
            vaeRef,
            decodeConfig,
            outputWidth,
            outputHeight,
            outputFrames,
            outputFps);

        if (result is not null)
        {
            g.CurrentMedia = result.ToWGNodeData(g);
        }
    }

}
