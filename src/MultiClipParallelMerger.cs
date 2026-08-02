using ComfyTyped.Core;
using ComfyTyped.Generated;
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

internal sealed class MultiClipParallelMerger(
    WorkflowGenerator g,
    IReadOnlyDictionary<ArchitectureId, IArchitectureBoundaryAssembler> boundaryAssemblers = null)
{
    private sealed record ArchitectureMergeRun(
        int Start,
        int Length,
        IArchitectureBoundaryAssembler Assembler,
        BoundaryOverlapPlan Overlap);

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
        IReadOnlyList<ArchitectureMergeRun> architectureRuns = [];
        if (overlapPlan is not null
            && !TryPreflightArchitectureRuns(
                generatedClips,
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
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: clip {clips[i].ClipId} cannot discard its {handle}-frame "
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
        MultiClipAudioGraphAssembler.TimelineAudioPreflight audioPreflight =
            MultiClipAudioGraphAssembler.PreflightTimelineAudio(
                bridge,
                generatedClips);

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
                generatedClips,
                audioPreflight);
        if (audioOutputs.Count > 0 && discardedHandles.Any(handle => handle > 0))
        {
            List<INodeOutput> trimmedAudio = [.. audioOutputs];
            for (int i = 0; i < discardedHandles.Length; i++)
            {
                int handle = discardedHandles[i];
                if (handle <= 0)
                {
                    continue;
                }
                TrimAudioDurationNode trim = bridge.AddNode(
                    new TrimAudioDurationNode().With(
                        StartIndex: handle / (double)clips[i].FramesPerSecond,
                        Duration: clips[i].Frames / (double)clips[i].FramesPerSecond));
                trim.Audio.ConnectToUntyped(trimmedAudio[i]);
                trimmedAudio[i] = trim.AUDIO;
            }
            audioOutputs = trimmedAudio;
        }
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
