using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Assembles the final timeline from planned boundaries and runtime downgrades.
/// <see cref="MultiClipParallelMerger"/> builds the graph.
/// </summary>
internal sealed class TimelineAssemblySession
{
    private readonly WorkflowGenerator _generator;
    private readonly MultiClipParallelMerger _merger;
    private readonly VideoExecutionPlan _plan;
    private readonly List<BoundaryPlan> _effectiveBoundaries;

    public TimelineAssemblySession(
        WorkflowGenerator generator,
        MultiClipParallelMerger merger,
        VideoExecutionPlan plan)
    {
        _generator = generator;
        _merger = merger;
        _plan = plan;
        _effectiveBoundaries = [.. plan.Boundaries];
    }

    public bool TryGetContinueInput(
        int fromClipId,
        out int handleFrames,
        out int windowFrames)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex >= 0
            && _effectiveBoundaries[boundaryIndex].Effective == BoundaryJoinType.Continue)
        {
            BoundaryPlan boundary = _effectiveBoundaries[boundaryIndex];
            handleFrames = BoundaryOverlapPlanner.IncomingHandleFrames(boundary);
            windowFrames = boundary.ContinuityWindowFrames;
            return true;
        }
        handleFrames = 0;
        windowFrames = 0;
        return false;
    }

    public bool TryGetAudioCarryWindow(int fromClipId, out int window)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex >= 0
            && _effectiveBoundaries[boundaryIndex] is BoundaryPlan
            {
                Effective: not BoundaryJoinType.Cut,
                CarryAudio: true,
            } boundary)
        {
            window = BoundaryOverlapPlanner.EffectiveOverlapFrames(boundary);
            return window > 0;
        }
        window = 0;
        return false;
    }

    public void DegradeToCut(int fromClipId)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex < 0
            || _effectiveBoundaries[boundaryIndex].Effective == BoundaryJoinType.Cut)
        {
            return;
        }
        _effectiveBoundaries[boundaryIndex] =
            BoundaryOverlapPlanner.DegradeToCut(_effectiveBoundaries[boundaryIndex]);
    }

    internal void ReportWarning(string warning) =>
        PlanDiagnosticReporter.TrackRequestWarning(_generator.UserInput, warning);

    public RuntimeArtifact Assemble(IReadOnlyList<DecodedClipArtifact> clipOutputs)
    {
        ArgumentNullException.ThrowIfNull(clipOutputs);
        if (clipOutputs.Count != _plan.Clips.Count)
        {
            throw Invariant.Failure(
                $"timeline assembly expected {_plan.Clips.Count} clip outputs "
                + $"but received {clipOutputs.Count}.");
        }
        TimelineMergeResult result = _merger.Merge(clipOutputs, _effectiveBoundaries);
        return result.Artifact;
    }

    /// <summary>
    /// Returns a single clip's decoded artifact as the timeline output, so publication uses the
    /// clip result instead of ambient media.
    /// </summary>
    public RuntimeArtifact FinalizeSingleClip(DecodedClipArtifact clipOutput)
    {
        ArgumentNullException.ThrowIfNull(clipOutput);
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        return RuntimeArtifact.FromDecoded(_generator, bridge, clipOutput);
    }

    private int BoundaryIndex(int fromClipId) =>
        _effectiveBoundaries.FindIndex(boundary => boundary.FromClipId == fromClipId);

}
