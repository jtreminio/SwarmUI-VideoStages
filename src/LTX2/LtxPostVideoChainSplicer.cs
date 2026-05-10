using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal static class LtxPostVideoChainSplicer
{
    public static void SpliceCurrentOutput(LtxPostVideoChainCapture capture, WorkflowGenerator g, WGNodeData vae)
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 })
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LtxChainCapture chainCapture = capture.BuildChainCapture(bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        MediaRef vaeRef =
            MediaRef.FromWGNodeData(vae, bridge) ?? MediaRef.FromWGNodeData(g.CurrentVae, bridge);
        LtxChainOps.DecodeConfig decodeConfig = capture.BuildDecodeConfig();

        MediaRef result =
            LtxChainOps.SpliceCurrentOutput(bridge, chainCapture, stageOutput, vaeRef, decodeConfig);
        BridgeSync.SyncLastId(g);

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

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LtxChainCapture chainCapture = capture.BuildChainCapture(bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        MediaRef vaeRef =
            MediaRef.FromWGNodeData(vae, bridge) ?? MediaRef.FromWGNodeData(g.CurrentVae, bridge);
        LtxChainOps.DecodeConfig decodeConfig = capture.BuildDecodeConfig();

        MediaRef result = LtxChainOps.SpliceCurrentOutputToDedicatedBranch(
            bridge,
            chainCapture,
            stageOutput,
            vaeRef,
            decodeConfig,
            outputWidth,
            outputHeight,
            outputFrames,
            outputFps);
        BridgeSync.SyncLastId(g);

        if (result is not null)
        {
            g.CurrentMedia = result.ToWGNodeData(g);
        }
    }

    public static void RetargetAnimationSaves(
        LtxPostVideoChainCapture capture,
        WorkflowGenerator g,
        JArray newImagePath)
    {
        if (newImagePath is not { Count: 2 })
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput oldOutput = bridge.ResolvePath(capture.CurrentOutputMedia.Path);
        INodeOutput newOutput = bridge.ResolvePath(newImagePath);
        if (oldOutput is null || newOutput is null)
        {
            return;
        }

        LtxChainOps.RetargetAnimationSaves(bridge, oldOutput, newOutput);
        BridgeSync.SyncLastId(g);
    }
}
