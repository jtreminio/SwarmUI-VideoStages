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
            && _effectiveBoundaries[boundaryIndex].Effective == BoundaryExecutionMode.Continue)
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
                Effective: not BoundaryExecutionMode.Cut,
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
            || _effectiveBoundaries[boundaryIndex].Effective == BoundaryExecutionMode.Cut)
        {
            return;
        }
        _effectiveBoundaries[boundaryIndex] =
            BoundaryOverlapPlanner.DegradeToCut(_effectiveBoundaries[boundaryIndex]);
    }

    internal void ReportWarning(string warning) =>
        PlanDiagnosticReporter.TrackRequestWarning(_generator.UserInput, warning);

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
    /// Installs the one clip's decoded artifact as the timeline output, so publication reads what
    /// the clip actually returned rather than whatever ambient media survived execution. A
    /// sourced-only clip additionally has no stage finalizer, so assembly owns its terminal trim.
    /// </summary>
    public void FinalizeSingleClip(DecodedClipArtifact clipOutput)
    {
        ArgumentNullException.ThrowIfNull(clipOutput);
        if (_plan.Clips.Count != 1)
        {
            throw new InvalidOperationException(
                "Only a single-clip timeline can use single-clip finalization.");
        }
        if (clipOutput.HasVideo != true)
        {
            throw new SwarmUserErrorException(
                "VideoStages: timeline assembly received an invalid clip video artifact.");
        }
        _generator.CurrentMedia = clipOutput.ToHostMedia(_generator);
        if (_plan.Clips[0].Stages.Count == 0)
        {
            _outputTrimmer.Apply();
        }
    }

    private int BoundaryIndex(int fromClipId) =>
        _plan.Boundaries.Select((boundary, index) => (boundary, index))
            .Where(entry => entry.boundary.FromClipId == fromClipId)
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .Single();

}
