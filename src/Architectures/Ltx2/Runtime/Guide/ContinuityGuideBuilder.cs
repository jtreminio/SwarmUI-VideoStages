using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Builds the opening-frame guide for a planned continue boundary. The guide keeps the previous
/// clip's resolution; each consuming stage performs its own spatial conform.
/// </summary>
internal sealed class ContinuityGuideBuilder(WorkflowGenerator g)
{
    public WGNodeData TryBuild(
        ClipPlan previousClip,
        WGNodeData previousOutput,
        int window,
        Timeline.Geometry nextGeometry)
    {
        ArgumentNullException.ThrowIfNull(previousClip);
        ArgumentNullException.ThrowIfNull(nextGeometry);

        int? frames = previousOutput?.Frames ?? previousClip.Frames;
        if (previousOutput?.Path is not JArray previousOutputPath
            || frames is not int lastFrameCount
            || lastFrameCount <= 0)
        {
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' needs a known frame count for the "
                + "previous clip's output; treating the boundary as a cut.");
            return null;
        }
        // Convert the target-grid window to the same duration on the source frame grid.
        int previousFps = previousOutput.GetRawFPS() is int rawFps && rawFps > 0
            ? rawFps
            : nextGeometry.FramesPerSecond;
        int sourceWindow = previousFps == nextGeometry.FramesPerSecond
            ? window
            : Math.Max(1, (int)Math.Round(
                window / (double)nextGeometry.FramesPerSecond * previousFps));
        if (sourceWindow > lastFrameCount)
        {
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' needs {sourceWindow} overlap frames but "
                + $"its output has {lastFrameCount}; treating the boundary as a cut.");
            return null;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageFromBatchNode tailFrames = bridge.AddNode(new ImageFromBatchNode().With(
            BatchIndex: lastFrameCount - sourceWindow,
            Length: sourceWindow));
        tailFrames.Image.TryConnectFromPath(bridge, previousOutputPath);
        return ConformTail(
            bridge,
            tailFrames.IMAGE,
            previousOutput,
            previousFps,
            window,
            nextGeometry);
    }

    /// <summary>
    /// Conforms the carried tail to the next clip's frame rate. Spatial conform remains with each
    /// consuming stage, preserving source resolution until then.
    /// </summary>
    private WGNodeData ConformTail(
        WorkflowBridge bridge,
        INodeOutput tail,
        WGNodeData previousOutput,
        int previousFps,
        int window,
        Timeline.Geometry nextGeometry)
    {
        INodeOutput conformed = tail;
        if (previousFps != nextGeometry.FramesPerSecond)
        {
            SwarmVideoResampleFPSNode resample = bridge.AddNode(new SwarmVideoResampleFPSNode().With(
                FpsIn: (double)previousFps,
                FpsOut: (double)nextGeometry.FramesPerSecond,
                Method: "linear"));
            resample.ImagesInput.ConnectToUntyped(conformed);
            conformed = resample.Images;
        }
        return new WGNodeData(
            WorkflowBridge.ToPath(conformed),
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
