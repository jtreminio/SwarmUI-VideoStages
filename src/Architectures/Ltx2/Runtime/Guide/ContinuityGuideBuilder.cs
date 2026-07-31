using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Builds the runtime opening-frame guide required by a planned continue boundary. Everything here
/// is clip-level and runs once: the tail slice and the frame-rate conform depend only on timeline
/// properties, so they cannot change between the next clip's stages. The tail keeps the previous
/// clip's native resolution; each stage that re-applies it conforms it to that stage's own
/// resolution (<see cref="GuideMediaPreparation.Prepare"/>), so the final stage anchors on
/// full-fidelity frames instead of a downscale the first stage happened to need.
/// </summary>
internal sealed class ContinuityGuideBuilder(WorkflowGenerator g)
{
    public WGNodeData TryBuild(
        ClipPlan previousClip,
        WGNodeData previousOutput,
        ClipPlan nextClip,
        int window,
        TimelineGeometry nextGeometry)
    {
        ArgumentNullException.ThrowIfNull(previousClip);
        ArgumentNullException.ThrowIfNull(nextClip);
        ArgumentNullException.ThrowIfNull(nextGeometry);

        StagePlan firstStage = nextClip.Stages.FirstOrDefault();
        if (firstStage is null
            || !Ltx2ModelCompatibility.IsLtxV2VideoModel(
                firstStage.ResolvedModel?.ModelName))
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' needs the next clip's first stage "
                + "on an LTX-2 model; treating the boundary as a cut.");
            return null;
        }
        if (firstStage.HasExplicitFirstFrameReference())
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Clip {nextClip.ClipId} has an explicit first-frame reference, which overrides the "
                + $"incoming 'continue' boundary from clip {previousClip.ClipId}; treating the boundary as a cut.");
            return null;
        }

        int? frames = previousOutput?.Frames ?? previousClip.Frames;
        if (previousOutput?.Path is not JArray previousOutputPath
            || frames is not int lastFrameCount
            || lastFrameCount <= 0)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' needs a known frame count for the "
                + "previous clip's output; treating the boundary as a cut.");
            return null;
        }
        // The tail is conditioning input to the next clip's generation, so it is expressed on the
        // next clip's grid: `window` frames there is a duration, and at the previous clip's own
        // frame rate that same duration spans a different number of frames.
        int previousFps = previousOutput.GetRawFPS() is int rawFps && rawFps > 0
            ? rawFps
            : nextGeometry.FramesPerSecond;
        int sourceWindow = previousFps == nextGeometry.FramesPerSecond
            ? window
            : Math.Max(1, (int)Math.Round(
                window / (double)nextGeometry.FramesPerSecond * previousFps));
        if (sourceWindow > lastFrameCount)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
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
    /// Conforms the carried tail to the next clip's frame rate, which wins at a join because the tail
    /// only exists as conditioning for that clip's generation. This runs on pixel frames, so no
    /// decode/re-encode round trip is involved. The spatial conform is deliberately absent: it belongs
    /// to whichever stage consumes the tail, and doing it here would pin every stage to the first
    /// stage's resolution.
    /// </summary>
    private WGNodeData ConformTail(
        WorkflowBridge bridge,
        INodeOutput tail,
        WGNodeData previousOutput,
        int previousFps,
        int window,
        TimelineGeometry nextGeometry)
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
