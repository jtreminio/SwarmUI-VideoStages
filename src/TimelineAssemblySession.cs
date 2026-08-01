using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Owns final timeline assembly. The immutable execution plan chooses each boundary; runtime
/// conditions can explicitly downgrade a boundary.
/// Graph construction remains in <see cref="MultiClipParallelMerger"/>.
/// </summary>
internal sealed class TimelineAssemblySession
{
    private readonly WorkflowGenerator _generator;
    private readonly MultiClipParallelMerger _merger;
    private readonly GlobalVideoFrameTrimmer _outputTrimmer;
    private readonly VideoExecutionPlan _plan;
    private readonly List<BoundaryPlan> _effectiveBoundaries;

    public TimelineAssemblySession(
        WorkflowGenerator generator,
        MultiClipParallelMerger merger,
        VideoExecutionPlan plan)
    {
        _generator = generator;
        _merger = merger;
        _outputTrimmer = new GlobalVideoFrameTrimmer(generator);
        _plan = plan;
        _effectiveBoundaries = [.. plan.Boundaries];
    }

    public bool TryGetContinueWindow(int fromClipId, out int window)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex >= 0
            && _effectiveBoundaries[boundaryIndex].Effective == BoundaryJoinType.Continue)
        {
            window = _effectiveBoundaries[boundaryIndex].ContinuityWindowFrames;
            return true;
        }
        window = 0;
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
            throw new SwarmUserErrorException(
                $"VideoStages: timeline assembly expected {_plan.Clips.Count} clip outputs "
                + $"but received {clipOutputs.Count}.");
        }
        TimelineMergeResult result = _merger.Merge(clipOutputs, _effectiveBoundaries);
        return _outputTrimmer.Apply(result.Artifact);
    }

    /// <summary>
    /// Returns a single clip's decoded artifact as the timeline output, so publication uses the
    /// clip result instead of ambient media. An init-video-only clip has no stage finalizer, so
    /// assembly owns its terminal trim.
    /// </summary>
    public RuntimeArtifact FinalizeSingleClip(DecodedClipArtifact clipOutput)
    {
        ArgumentNullException.ThrowIfNull(clipOutput);
        if (clipOutput.HasVideo != true)
        {
            throw new SwarmUserErrorException(
                "VideoStages: timeline assembly received an invalid clip video artifact.");
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        RuntimeArtifact artifact = RuntimeArtifact.FromDecoded(
            _generator,
            bridge,
            clipOutput);
        if (_plan.Clips[0].Stages.Count == 0)
        {
            artifact = _outputTrimmer.Apply(artifact);
        }
        return artifact;
    }

    private int BoundaryIndex(int fromClipId) =>
        _plan.Boundaries.Select((boundary, index) => (boundary, index))
            .Where(entry => entry.boundary.FromClipId == fromClipId)
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .Single();

}
