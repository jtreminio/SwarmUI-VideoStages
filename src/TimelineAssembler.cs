using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Owns final timeline assembly. The immutable execution plan chooses each boundary; the running
/// sequence can explicitly downgrade a boundary when a required runtime artifact is unavailable.
/// Graph construction remains in <see cref="MultiClipParallelMerger"/>.
/// </summary>
internal sealed class TimelineAssembler(
    WorkflowGenerator g,
    MultiClipParallelMerger merger)
{
    internal TimelineAssemblySession Begin(VideoExecutionPlan plan) => new(g, merger, plan);
}

internal sealed class TimelineAssemblySession
{
    private readonly MultiClipParallelMerger _merger;
    private readonly GlobalVideoFrameTrimmer _outputTrimmer;
    private readonly VideoExecutionPlan _plan;
    private readonly List<BoundaryPlan> _effectiveBoundaries;

    public TimelineAssemblySession(
        WorkflowGenerator generator,
        MultiClipParallelMerger merger,
        VideoExecutionPlan plan)
    {
        _merger = merger;
        _outputTrimmer = new GlobalVideoFrameTrimmer(generator);
        _plan = plan;
        _effectiveBoundaries = [.. plan.Boundaries];
    }

    public bool TryGetContinueWindow(int fromClipId, out int window)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex >= 0
            && _effectiveBoundaries[boundaryIndex].Effective == BoundaryExecutionMode.Continue)
        {
            window = _effectiveBoundaries[boundaryIndex].ContinuityWindowFrames;
            return true;
        }
        window = 0;
        return false;
    }

    public void DegradeToCut(int fromClipId, string reason)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex < 0
            || _effectiveBoundaries[boundaryIndex].Effective == BoundaryExecutionMode.Cut)
        {
            return;
        }
        _effectiveBoundaries[boundaryIndex] =
            BoundaryOverlapPlanner.DegradeToCut(_effectiveBoundaries[boundaryIndex]);
        Logs.Warning(
            $"VideoStages: clip {fromClipId} continuity boundary degraded to a cut because {reason}.");
    }

    public void Assemble(IReadOnlyList<DecodedClipArtifact> clipOutputs)
    {
        ArgumentNullException.ThrowIfNull(clipOutputs);
        if (clipOutputs.Count != _plan.Clips.Count)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: timeline assembly expected {_plan.Clips.Count} clip outputs "
                + $"but received {clipOutputs.Count}.");
        }
        if (clipOutputs.Any(output => output?.HasVideo != true))
        {
            throw new SwarmUserErrorException(
                "VideoStages: timeline assembly received an invalid clip video artifact.");
        }
        if (clipOutputs.Count < 2)
        {
            return;
        }

        BoundaryBudgetResolution resolution = _merger.Apply(clipOutputs, _effectiveBoundaries);
        _effectiveBoundaries.Clear();
        _effectiveBoundaries.AddRange(resolution.Boundaries);
        _outputTrimmer.Apply();
    }

    /// <summary>
    /// A sourced-only one-clip timeline has no stage finalizer, so timeline assembly owns its
    /// one terminal trim explicitly.
    /// </summary>
    public void FinalizeUnstagedSingleClip()
    {
        if (_plan.Clips.Count != 1 || _plan.Clips[0].Stages.Count != 0)
        {
            throw new InvalidOperationException(
                "Only an unstaged single-clip timeline can use sourced-only finalization.");
        }
        _outputTrimmer.Apply();
    }

    private int BoundaryIndex(int fromClipId) =>
        _plan.Boundaries.Select((boundary, index) => (boundary, index))
            .Where(entry => entry.boundary.FromClipId == fromClipId)
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .Single();

}
