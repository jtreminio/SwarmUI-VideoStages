using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Timeline;

/// <summary>
/// The run's boundaries: planned joins plus whatever the architectures degraded at runtime. Merges
/// the finished clips on those boundaries, with <see cref="Merger"/> building the graph.
/// </summary>
internal sealed class Boundaries
{
    private readonly WorkflowGenerator _generator;
    private readonly Merger _merger;
    private readonly VideoExecutionPlan _plan;
    private readonly List<BoundaryPlan> _effectiveBoundaries;

    public Boundaries(
        WorkflowGenerator generator,
        Merger merger,
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

    public RuntimeArtifact Merge(IReadOnlyList<DecodedClipArtifact> clipOutputs)
    {
        ArgumentNullException.ThrowIfNull(clipOutputs);
        if (clipOutputs.Count != _plan.Clips.Count)
        {
            throw Invariant.Failure(
                $"timeline merge expected {_plan.Clips.Count} clip outputs "
                + $"but received {clipOutputs.Count}.");
        }
        if (clipOutputs.Count == 1)
        {
            // A single clip has no boundary to merge, but publication must still use the clip
            // result rather than ambient media.
            using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
            return RuntimeArtifact.FromDecoded(_generator, bridge, clipOutputs[0]);
        }
        return _merger.Merge(clipOutputs, _effectiveBoundaries);
    }

    private int BoundaryIndex(int fromClipId) =>
        _effectiveBoundaries.FindIndex(boundary => boundary.FromClipId == fromClipId);
}
