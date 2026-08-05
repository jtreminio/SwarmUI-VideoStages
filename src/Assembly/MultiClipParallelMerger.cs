using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

internal sealed record TimelineMergeResult(
    BoundaryBudgetResolution Boundaries,
    RuntimeArtifact Artifact);

internal sealed class MultiClipParallelMerger(WorkflowGenerator g)
{
    internal TimelineMergeResult Merge(
        IReadOnlyList<DecodedClipArtifact> clipArtifacts,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        List<INodeOutput> resolvedOutputs =
            ResolveOutputs(bridge, clipArtifacts.Select(clip => clip.Video.ToPath()));
        if (resolvedOutputs.Count != clipArtifacts.Count)
        {
            throw Invariant.Failure(
                $"timeline assembly could resolve only {resolvedOutputs.Count} of "
                + $"{clipArtifacts.Count} planned clip video outputs.");
        }

        // Conform before overlap planning so every downstream graph uses the same geometry.
        TimelineGeometryConform.ConformResult conform = TimelineGeometryConform.Apply(
            bridge,
            clipArtifacts,
            resolvedOutputs,
            boundaries);
        PlanDiagnosticReporter.ThrowIfBlocking(
            conform.Diagnostics,
            "VideoStages timeline assembly");
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
        int sumFrames = clips.Sum(clip => clip.Frames);
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
            Frames = sumFrames - (overlapPlan?.RemovedFrames ?? 0),
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
        return new(
            runtimeBoundaries,
            new(
                mergedMedia,
                MediaRef.FromWGNodeData(g.CurrentVae, bridge)));
    }

    private static List<INodeOutput> ResolveOutputs(WorkflowBridge bridge, IEnumerable<JArray> paths)
    {
        List<INodeOutput> outputs = [];
        foreach (JArray path in paths)
        {
            INodeOutput output = bridge.ResolvePath(path);
            if (output is not null)
            {
                outputs.Add(output);
            }
        }
        return outputs;
    }
}
