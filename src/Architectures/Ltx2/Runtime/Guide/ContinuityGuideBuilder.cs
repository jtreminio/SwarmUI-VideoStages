using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Builds the runtime opening-frame guide required by a planned continue boundary.</summary>
internal sealed class ContinuityGuideBuilder(WorkflowGenerator g)
{
    public WGNodeData TryBuild(
        ClipPlan previousClip,
        WGNodeData previousOutput,
        ClipPlan nextClip,
        int window)
    {
        ArgumentNullException.ThrowIfNull(previousClip);
        ArgumentNullException.ThrowIfNull(nextClip);

        StagePlan firstStage = nextClip.Stages.FirstOrDefault();
        if (firstStage is null
            || !Ltx2ModelCompatibility.IsLtxV2VideoModel(
                firstStage.RequireLtx2Payload().Core.Model))
        {
            Logs.Warning(
                $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' needs the next clip's first stage "
                + "on an LTX-2 model; treating the boundary as a cut.");
            return null;
        }
        if (firstStage.RequireLtx2Payload().FrameReferences.Any(reference =>
            reference.FrameOrigin == ImageReferenceFrameOrigin.Start && reference.Frame == 1))
        {
            Logs.Warning(
                $"VideoStages: Clip {nextClip.ClipId} has an explicit first-frame reference, which overrides the "
                + $"incoming 'continue' boundary from clip {previousClip.ClipId}; treating the boundary as a cut.");
            return null;
        }

        int? frames = previousOutput?.Frames ?? previousClip.Frames;
        if (previousOutput?.Path is not JArray previousOutputPath
            || frames is not int lastFrameCount
            || lastFrameCount <= 0)
        {
            Logs.Warning(
                $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' needs a known frame count for the "
                + "previous clip's output; treating the boundary as a cut.");
            return null;
        }
        if (window > lastFrameCount)
        {
            Logs.Warning(
                $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' needs {window} overlap frames but "
                + $"its output has {lastFrameCount}; treating the boundary as a cut.");
            return null;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageFromBatchNode tailFrames = bridge.AddNode(new ImageFromBatchNode().With(
            BatchIndex: lastFrameCount - window,
            Length: window));
        tailFrames.Image.TryConnectFromPath(bridge, previousOutputPath);
        return new WGNodeData(
            WorkflowBridge.ToPath(tailFrames.IMAGE),
            g,
            WGNodeData.DT_IMAGE,
            previousOutput.Compat)
        {
            Width = previousOutput.Width,
            Height = previousOutput.Height,
            Frames = window
        };
    }
}
