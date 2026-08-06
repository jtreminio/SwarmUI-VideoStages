using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed record MiniMaxBoundaryReference(JArray Video, JArray Audio);

internal sealed class MiniMaxBoundaryReferenceBuilder(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    Timeline.Boundaries boundaries)
{
    internal MiniMaxBoundaryReference TryBuild(
        ArchitectureClipRuntimeContext context,
        int targetFrames)
    {
        if (context.PreviousClip is null
            || !boundaries.TryGetReferenceContinueInput(
                context.PreviousClip.ClipId,
                out int windowFrames,
                out double scale,
                out bool includeSoundtrack))
        {
            return null;
        }
        DecodedClipArtifact previous = context.PreviousClipOutput;
        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput previousVideo = previous?.Video?.Resolve(bridge);
        INodeOutput previousAudio = includeSoundtrack
            ? previous?.Audio?.Resolve(bridge)
            : null;
        int targetFps = plan.FramesPerSecond;
        int sourceFps = previous?.FramesPerSecond ?? 0;
        int requestedReferenceFrames = targetFps > 0
            ? Math.Max(
                MiniMaxArchitectureModule.FrameGridOrigin,
                (int)Math.Round(
                    windowFrames / (double)targetFps
                        * MiniMaxArchitectureModule.ReferenceFramesPerSecond))
            : 0;
        int cappedReferenceFrames = Math.Min(
            requestedReferenceFrames,
            targetFrames);
        int referenceFrames = cappedReferenceFrames <= 0
            ? 0
            : MiniMaxArchitectureModule.FrameGridOrigin
                + (cappedReferenceFrames - MiniMaxArchitectureModule.FrameGridOrigin)
                    / MiniMaxArchitectureModule.FrameGrid
                    * MiniMaxArchitectureModule.FrameGrid;
        int sourceWindow = sourceFps > 0 && targetFps > 0
            ? Math.Max(1, (int)Math.Ceiling(
                referenceFrames / MiniMaxArchitectureModule.ReferenceFramesPerSecond * sourceFps))
            : 0;
        if (previousVideo is null
            || (includeSoundtrack && previousAudio is null)
            || sourceFps <= 0
            || targetFps <= 0
            || referenceFrames <= 0
            || sourceWindow > previous.Frames)
        {
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: Clip {context.PreviousClip.ClipId} cannot supply "
                    + (includeSoundtrack ? "video and audio " : "video ")
                    + $"reference context to Clip {context.Clip.ClipId}; treating the boundary as a cut.");
            boundaries.DegradeToCut(context.PreviousClip.ClipId);
            return null;
        }

        ImageFromBatchNode tail = bridge.AddNode(new ImageFromBatchNode().With(
            BatchIndex: previous.Frames - sourceWindow,
            Length: sourceWindow));
        tail.Image.ConnectToUntyped(previousVideo);
        INodeOutput video = tail.IMAGE;
        if (sourceFps != MiniMaxArchitectureModule.ReferenceFramesPerSecond)
        {
            SwarmVideoResampleFPSNode resample = bridge.AddNode(
                new SwarmVideoResampleFPSNode().With(
                    FpsIn: (double)sourceFps,
                    FpsOut: MiniMaxArchitectureModule.ReferenceFramesPerSecond,
                    Method: "linear"));
            resample.ImagesInput.ConnectToUntyped(video);
            video = resample.Images;
            int resampledFrames = Math.Max(1, (int)Math.Round(
                sourceWindow / (double)sourceFps
                    * MiniMaxArchitectureModule.ReferenceFramesPerSecond));
            if (resampledFrames > referenceFrames)
            {
                ImageFromBatchNode aligned = bridge.AddNode(new ImageFromBatchNode().With(
                    BatchIndex: resampledFrames - referenceFrames,
                    Length: referenceFrames));
                aligned.Image.ConnectToUntyped(video);
                video = aligned.IMAGE;
            }
        }

        if (scale < 1)
        {
            ImageScaleByNode scaled = bridge.AddNode(new ImageScaleByNode().With(
                UpscaleMethod: "lanczos",
                ScaleBy: scale));
            scaled.Image.ConnectToUntyped(video);
            video = scaled.IMAGE;
        }
        JArray audioPath = null;
        if (includeSoundtrack)
        {
            double duration =
                referenceFrames / MiniMaxArchitectureModule.ReferenceFramesPerSecond;
            TrimAudioDurationNode audio = bridge.AddNode(new TrimAudioDurationNode().With(
                StartIndex: previous.Frames / (double)sourceFps - duration,
                Duration: duration));
            audio.Audio.ConnectToUntyped(previousAudio);
            audioPath = WorkflowBridge.ToPath(audio.AUDIO);
        }
        return new(WorkflowBridge.ToPath(video), audioPath);
    }

}
