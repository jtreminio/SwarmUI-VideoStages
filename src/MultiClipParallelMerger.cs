using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

internal sealed record TimelineMergeResult(
    BoundaryBudgetResolution Boundaries,
    RuntimeArtifact Artifact);

/// <summary>
/// Resolves clip graphs into a timeline artifact. <see cref="Apply"/> also publishes the result to
/// host state for legacy callers.
/// </summary>
internal sealed class MultiClipParallelMerger(
    WorkflowGenerator g,
    IReadOnlyDictionary<ArchitectureId, IArchitectureBoundaryAssembler> boundaryAssemblers = null)
{
    private sealed record ArchitectureMergeRun(
        int Start,
        int Length,
        IArchitectureBoundaryAssembler Assembler,
        BoundaryOverlapPlan Overlap);

    public BoundaryBudgetResolution Apply(
        IReadOnlyList<DecodedClipArtifact> clipArtifacts,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        TimelineMergeResult result = Merge(clipArtifacts, boundaries);
        result.Artifact?.PublishTo(g);
        return result.Boundaries;
    }

    internal TimelineMergeResult Merge(
        IReadOnlyList<DecodedClipArtifact> clipArtifacts,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        List<INodeOutput> resolvedOutputs =
            ResolveOutputs(bridge, clipArtifacts.Select(clip => clip.Video.ToPath()));
        if (resolvedOutputs.Count != clipArtifacts.Count)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: timeline assembly could resolve only {resolvedOutputs.Count} of "
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
        IReadOnlyList<DecodedClipArtifact> clips = conform.Clips;
        IReadOnlyList<INodeOutput> videoOutputs = conform.VideoOutputs;
        int sumFrames = clips.Sum(clip => clip.Frames);

        BoundaryBudgetResolution runtimeBoundaries =
            BoundaryOverlapPlanner.ValidateRuntime(clips, conform.Boundaries);
        if (runtimeBoundaries.Degraded)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: overlap boundaries degraded to cuts because "
                + $"{runtimeBoundaries.Reason}.");
        }
        BoundaryOverlapPlan overlapPlan =
            BoundaryOverlapPlanner.ToOverlapPlan(runtimeBoundaries.Boundaries);
        IReadOnlyList<ArchitectureMergeRun> architectureRuns = [];
        if (overlapPlan is not null
            && !TryPreflightArchitectureRuns(
                clips,
                runtimeBoundaries.Boundaries,
                out architectureRuns,
                out string preflightFailure))
        {
            runtimeBoundaries = BoundaryOverlapPlanner.DegradeAllToCuts(
                runtimeBoundaries.Boundaries,
                preflightFailure);
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: overlap boundaries degraded to cuts because {preflightFailure}.");
            overlapPlan = null;
        }
        MultiClipAudioGraphAssembler.TimelineAudioPreflight audioPreflight =
            MultiClipAudioGraphAssembler.PreflightTimelineAudio(
                bridge,
                clips);

        INodeOutput mergedVideo = overlapPlan is null
            ? MultiClipVideoGraphAssembler.MergeCut(bridge, videoOutputs)
            : MergeArchitectureRuns(
                bridge,
                clips,
                videoOutputs,
                architectureRuns);

        IReadOnlyList<INodeOutput> audioOutputs =
            MultiClipAudioGraphAssembler.MaterializeTimelineAudio(
                bridge,
                clips,
                audioPreflight);
        INodeOutput mergedAudio = audioOutputs.Count > 0
            ? MultiClipAudioGraphAssembler.Merge(
                bridge,
                clips,
                audioOutputs,
                overlapPlan)
            : null;

        DecodedClipArtifact template = clips[0];
        MediaRef mergedMedia = new()
        {
            Output = mergedVideo,
            DataType = WGNodeData.DT_VIDEO,
            Width = template.Width,
            Height = template.Height,
            Frames = sumFrames - (overlapPlan?.RemovedFrames ?? 0),
            FPS = template.FramesPerSecond
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

    private static INodeOutput MergeArchitectureRuns(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> videoOutputs,
        IReadOnlyList<ArchitectureMergeRun> runs)
    {
        List<INodeOutput> runOutputs = new(runs.Count);
        foreach (ArchitectureMergeRun run in runs)
        {
            if (run.Length == 1)
            {
                runOutputs.Add(videoOutputs[run.Start]);
                continue;
            }
            runOutputs.Add(run.Assembler.MergeOverlaps(
                bridge,
                [.. clips.Skip(run.Start).Take(run.Length)],
                [.. videoOutputs.Skip(run.Start).Take(run.Length)],
                run.Overlap));
        }
        return MultiClipVideoGraphAssembler.MergeCut(bridge, runOutputs);
    }

    private bool TryPreflightArchitectureRuns(
        IReadOnlyList<DecodedClipArtifact> artifacts,
        IReadOnlyList<BoundaryPlan> boundaries,
        out IReadOnlyList<ArchitectureMergeRun> runs,
        out string failure)
    {
        if (boundaries.Count != artifacts.Count - 1)
        {
            runs = [];
            failure = "the decoded clip count does not match the boundary count";
            return false;
        }

        List<ArchitectureMergeRun> resolved = [];
        int runStart = 0;
        for (int boundaryIndex = 0; boundaryIndex <= boundaries.Count; boundaryIndex++)
        {
            bool endOfTimeline = boundaryIndex == boundaries.Count;
            if (!endOfTimeline
                && boundaries[boundaryIndex].Effective != BoundaryJoinType.Cut)
            {
                continue;
            }

            int runEndExclusive = boundaryIndex + 1;
            int runLength = runEndExclusive - runStart;
            if (runLength == 1)
            {
                resolved.Add(new(runStart, runLength, null, null));
                runStart = runEndExclusive;
                continue;
            }

            ArchitectureId architectureId = artifacts[runStart].ArchitectureId;
            if (artifacts
                .Skip(runStart)
                .Take(runLength)
                .Any(artifact => artifact.ArchitectureId != architectureId))
            {
                runs = [];
                failure = "an overlap crosses architecture ownership";
                return false;
            }
            if (boundaryAssemblers is null
                || !boundaryAssemblers.TryGetValue(
                    architectureId,
                    out IArchitectureBoundaryAssembler assembler))
            {
                runs = [];
                failure = $"architecture '{architectureId}' has no boundary assembler";
                return false;
            }
            BoundaryOverlapPlan runPlan = BoundaryOverlapPlanner.ToOverlapPlan(
                [.. boundaries.Skip(runStart).Take(runLength - 1)]);
            if (runPlan is null)
            {
                runs = [];
                failure = "an overlap run has no overlap plan";
                return false;
            }
            resolved.Add(new(runStart, runLength, assembler, runPlan));
            runStart = runEndExclusive;
        }
        runs = resolved;
        failure = null;
        return true;
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
