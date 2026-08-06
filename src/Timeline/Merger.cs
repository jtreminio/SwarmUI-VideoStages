using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Timeline;

/// <summary>
/// Builds the timeline's single video and audio output from the clips that ran. Conforms geometry,
/// re-resolves boundaries against real clip lengths, discards pre-roll a degraded boundary left
/// behind, then hands each stream to its joiner.
/// </summary>
internal sealed class Merger(WorkflowGenerator g)
{
    internal RuntimeArtifact Merge(
        IReadOnlyList<DecodedClipArtifact> clipArtifacts,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        List<INodeOutput> resolvedOutputs = new(clipArtifacts.Count);
        foreach (DecodedClipArtifact clip in clipArtifacts)
        {
            resolvedOutputs.Add(bridge.ResolvePath(clip.Video.ToPath())
                ?? throw Invariant.Failure(
                    $"clip {clip.ClipId} left no video output in the workflow, so the timeline "
                    + "cannot be merged."));
        }

        // Conform before overlap planning so every downstream graph uses the same geometry.
        GeometryConform.ConformResult conform = GeometryConform.Apply(
            bridge,
            clipArtifacts,
            resolvedOutputs,
            boundaries);
        PlanDiagnosticReporter.ThrowIfBlocking(
            conform.Diagnostics,
            "VideoStages timeline merge");
        PlanDiagnosticReporter.ReportToRequest(conform.Diagnostics, g.UserInput);
        IReadOnlyList<DecodedClipArtifact> generatedClips = conform.Clips;
        IReadOnlyList<INodeOutput> generatedVideoOutputs = conform.VideoOutputs;
        IReadOnlyList<BoundaryPlan> generatedBoundaries = conform.Boundaries;

        BoundaryBudgetResolution runtimeBoundaries =
            BoundaryOverlapPlanner.ValidateRuntime(generatedClips, generatedBoundaries);
        if (runtimeBoundaries.Degraded)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: overlap boundaries degraded to cuts because "
                + $"{runtimeBoundaries.Reason}.");
        }
        BoundaryOverlapPlan overlapPlan =
            BoundaryOverlapPlanner.ToOverlapPlan(runtimeBoundaries.Boundaries);
        int[] discardedHandles = new int[generatedClips.Count];
        for (int i = 0; i < generatedBoundaries.Count; i++)
        {
            if (generatedBoundaries[i].Effective == BoundaryJoinType.Continue
                && runtimeBoundaries.Boundaries[i].Effective == BoundaryJoinType.Cut)
            {
                discardedHandles[i + 1] =
                    BoundaryOverlapPlanner.IncomingHandleFrames(generatedBoundaries[i]);
            }
        }

        // A Continue boundary that degraded to a cut leaves pre-roll frames in the incoming clip
        // that nothing consumes; they would play as duplicates of the previous clip's tail.
        List<DecodedClipArtifact> clips = [.. generatedClips];
        List<INodeOutput> videoOutputs = [.. generatedVideoOutputs];
        for (int i = 0; i < discardedHandles.Length; i++)
        {
            int handle = discardedHandles[i];
            if (handle <= 0)
            {
                continue;
            }
            if (clips[i].Frames <= handle)
            {
                throw Invariant.Failure(
                    $"clip {clips[i].ClipId} cannot discard its {handle}-frame "
                    + "Continue handle after a runtime fallback.");
            }
            ImageFromBatchNode trim = bridge.AddNode(new ImageFromBatchNode().With(
                BatchIndex: handle,
                Length: clips[i].Frames - handle));
            trim.Image.ConnectToUntyped(videoOutputs[i]);
            videoOutputs[i] = trim.IMAGE;
            clips[i] = clips[i] with { Frames = clips[i].Frames - handle };
        }
        INodeOutput mergedVideo = DecodedVideoJoiner.Merge(
            bridge,
            clips,
            videoOutputs,
            overlapPlan);

        IReadOnlyList<INodeOutput> audioOutputs =
            DecodedAudioJoiner.TrimDiscardedHandles(
                bridge,
                clips,
                DecodedAudioJoiner.MaterializeTimelineAudio(bridge, generatedClips),
                discardedHandles);
        INodeOutput mergedAudio = audioOutputs.Count > 0
            ? DecodedAudioJoiner.Merge(
                bridge,
                clips,
                audioOutputs,
                overlapPlan)
            : null;

        MediaRef mergedMedia = new()
        {
            Output = mergedVideo,
            DataType = WGNodeData.DT_VIDEO,
            Width = conform.Target.Width,
            Height = conform.Target.Height,
            Frames = clips.Sum(clip => clip.Frames) - (overlapPlan?.RemovedFrames ?? 0),
            FPS = conform.Target.FramesPerSecond
        };
        if (mergedAudio is not null)
        {
            mergedMedia.AttachedAudio = new MediaRef
            {
                Output = mergedAudio,
                DataType = WGNodeData.DT_AUDIO,
            };
        }
        return new(mergedMedia, MediaRef.FromWGNodeData(g.CurrentVae, bridge));
    }
}
