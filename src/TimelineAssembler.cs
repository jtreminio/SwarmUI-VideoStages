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

internal sealed record RuntimeBoundaryDegradation(int FromClipId, string Reason);

internal sealed class TimelineAssemblySession
{
    private readonly WorkflowGenerator _generator;
    private readonly MultiClipParallelMerger _merger;
    private readonly VideoExecutionPlan _plan;
    private readonly List<BoundaryExecutionMode> _effectiveModes;
    private readonly int[] _continueWindows;
    private readonly List<RuntimeBoundaryDegradation> _degradations = [];

    public IReadOnlyList<RuntimeBoundaryDegradation> RuntimeDegradations => _degradations;

    public TimelineAssemblySession(
        WorkflowGenerator generator,
        MultiClipParallelMerger merger,
        VideoExecutionPlan plan)
    {
        _generator = generator;
        _merger = merger;
        _plan = plan;
        _effectiveModes = [.. plan.Boundaries.Select(boundary => boundary.Effective)];
        _continueWindows = MultiClipParallelMerger.ResolveContinueWindows(
            [.. plan.Clips.Select(clip => clip.Frames)],
            [.. _effectiveModes.Select(ToBoundaryOut)],
            [.. plan.Boundaries.Select(boundary => boundary.OverlapFrames)]);
    }

    public bool TryGetContinueWindow(int fromClipId, out int window)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex >= 0 && _effectiveModes[boundaryIndex] == BoundaryExecutionMode.Continue)
        {
            window = _continueWindows[boundaryIndex];
            return true;
        }
        window = 0;
        return false;
    }

    public void DegradeToCut(int fromClipId, string reason)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex < 0 || _effectiveModes[boundaryIndex] == BoundaryExecutionMode.Cut)
        {
            return;
        }
        _effectiveModes[boundaryIndex] = BoundaryExecutionMode.Cut;
        _degradations.Add(new RuntimeBoundaryDegradation(fromClipId, reason));
    }

    public void Assemble(IReadOnlyList<RuntimeArtifact> clipOutputs)
    {
        ArgumentNullException.ThrowIfNull(clipOutputs);
        if (clipOutputs.Count != _plan.Clips.Count)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: timeline assembly expected {_plan.Clips.Count} clip outputs "
                + $"but received {clipOutputs.Count}.");
        }
        if (clipOutputs.Any(output => output?.HasMedia != true))
        {
            throw new SwarmUserErrorException(
                "VideoStages: timeline assembly received an invalid clip video artifact.");
        }
        if (clipOutputs.Count < 2)
        {
            return;
        }

        List<WGNodeData> media = [.. clipOutputs.Select(output => output.Media?.ToWGNodeData(_generator))];
        _merger.Apply(
            media,
            [.. _effectiveModes.Select(ToBoundaryOut)],
            continueWindows: _continueWindows,
            boundaryOverlapPrefs: [.. _plan.Boundaries.Select(boundary => boundary.OverlapFrames)]);
    }

    private int BoundaryIndex(int fromClipId) =>
        _plan.Boundaries.Select((boundary, index) => (boundary, index))
            .Where(entry => entry.boundary.FromClipId == fromClipId)
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .Single();

    private static string ToBoundaryOut(BoundaryExecutionMode mode) => mode switch
    {
        BoundaryExecutionMode.Continue => Constants.BoundaryOutContinue,
        BoundaryExecutionMode.Crossfade => Constants.BoundaryOutCrossfade,
        _ => Constants.BoundaryOutCut,
    };
}
