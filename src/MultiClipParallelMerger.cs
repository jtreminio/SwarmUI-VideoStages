using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Orchestrates graph resolution and media ownership for a multi-clip timeline. Boundary planning and
/// graph construction live in dedicated collaborators so this class remains the runner-facing facade.
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
        if (clipArtifacts is null || clipArtifacts.Count < 2)
        {
            return new(boundaries ?? [], Degraded: false, Reason: null);
        }
        for (int i = 0; i < clipArtifacts.Count; i++)
        {
            DecodedClipArtifact artifact = clipArtifacts[i];
            if (artifact?.HasVideo != true
                || artifact.Video.Kind != DecodedMediaKind.Video
                || (artifact.Audio is not null
                    && artifact.Audio.Kind != DecodedMediaKind.Audio)
                || artifact.Width <= 0
                || artifact.Height <= 0
                || artifact.Frames <= 0
                || artifact.FramesPerSecond <= 0)
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: clip {i} is missing decoded video metadata.");
            }
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        List<INodeOutput> resolvedOutputs =
            ResolveOutputs(bridge, clipArtifacts.Select(clip => clip.Video.ToPath()));
        if (resolvedOutputs.Count != clipArtifacts.Count)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: timeline assembly could resolve only {resolvedOutputs.Count} of "
                + $"{clipArtifacts.Count} planned clip video outputs.");
        }

        // Conforming runs before every consumer of clip geometry, so overlap merging, the concat,
        // and the published media all see one width, height, and frame rate.
        TimelineGeometryConform.ConformResult conform = TimelineGeometryConform.Apply(
            bridge,
            clipArtifacts,
            resolvedOutputs,
            boundaries);
        PlanDiagnosticReporter.ThrowIfBlocking(
            conform.Diagnostics,
            "VideoStages timeline assembly");
        PlanDiagnosticReporter.Report(conform.Diagnostics);
        IReadOnlyList<DecodedClipArtifact> clips = conform.Clips;
        IReadOnlyList<INodeOutput> videoOutputs = conform.VideoOutputs;
        int sumFrames = clips.Sum(clip => clip.Frames);

        BoundaryBudgetResolution runtimeBoundaries =
            BoundaryOverlapPlanner.ValidateRuntime(clips, conform.Boundaries);
        if (runtimeBoundaries.Degraded)
        {
            Logs.Warning(
                $"VideoStages: overlap boundaries degraded to cuts because "
                + $"{runtimeBoundaries.Reason}.");
        }
        BoundaryOverlapPlan overlapPlan =
            BoundaryOverlapPlanner.ToOverlapPlan(runtimeBoundaries.Boundaries);
        IReadOnlyList<ArchitectureMergeRun> architectureRuns = overlapPlan is null
            ? []
            : PreflightArchitectureRuns(
                clips,
                runtimeBoundaries.Boundaries);
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
        g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(mergedVideo), g, WGNodeData.DT_VIDEO, null)
        {
            Width = template.Width,
            Height = template.Height,
            Frames = sumFrames - (overlapPlan?.RemovedFrames ?? 0),
            FPS = template.FramesPerSecond
        };
        if (mergedAudio is not null)
        {
            g.CurrentMedia.AttachedAudio = new WGNodeData(
                WorkflowBridge.ToPath(mergedAudio),
                g,
                WGNodeData.DT_AUDIO,
                null);
        }
        return runtimeBoundaries;
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

    private IReadOnlyList<ArchitectureMergeRun> PreflightArchitectureRuns(
        IReadOnlyList<DecodedClipArtifact> artifacts,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        if (boundaries.Count != artifacts.Count - 1)
        {
            throw new InvalidOperationException(
                "Timeline boundary count does not match the decoded clip count.");
        }

        List<ArchitectureMergeRun> runs = [];
        int runStart = 0;
        for (int boundaryIndex = 0; boundaryIndex <= boundaries.Count; boundaryIndex++)
        {
            bool endOfTimeline = boundaryIndex == boundaries.Count;
            if (!endOfTimeline
                && boundaries[boundaryIndex].Effective != BoundaryExecutionMode.Cut)
            {
                continue;
            }

            int runEndExclusive = boundaryIndex + 1;
            int runLength = runEndExclusive - runStart;
            if (runLength == 1)
            {
                runs.Add(new(runStart, runLength, null, null));
                runStart = runEndExclusive;
                continue;
            }

            ArchitectureId architectureId = artifacts[runStart].ArchitectureId;
            if (artifacts
                .Skip(runStart)
                .Take(runLength)
                .Any(artifact => artifact.ArchitectureId != architectureId))
            {
                throw new InvalidOperationException(
                    "A non-cut boundary run crossed architecture ownership.");
            }
            if (boundaryAssemblers is null
                || !boundaryAssemblers.TryGetValue(
                    architectureId,
                    out IArchitectureBoundaryAssembler assembler))
            {
                throw new InvalidOperationException(
                    $"No boundary assembler is registered for architecture '{architectureId}'.");
            }
            if (assembler.ArchitectureId != architectureId)
            {
                throw new InvalidOperationException(
                    $"Boundary assembler '{assembler.ArchitectureId}' cannot assemble "
                    + $"architecture '{architectureId}'.");
            }

            BoundaryOverlapPlan runPlan = BoundaryOverlapPlanner.ToOverlapPlan(
                [.. boundaries.Skip(runStart).Take(runLength - 1)])
                ?? throw new InvalidOperationException(
                    "A non-cut architecture run has no overlap plan.");
            runs.Add(new(runStart, runLength, assembler, runPlan));
            runStart = runEndExclusive;
        }
        return runs;
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
