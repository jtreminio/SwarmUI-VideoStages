using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
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
            && _effectiveBoundaries[boundaryIndex].EffectiveJoin == BoundaryJoinType.Continue)
        {
            BoundaryPlan boundary = _effectiveBoundaries[boundaryIndex];
            handleFrames = BoundaryOverlaps.IncomingHandleFrames(boundary);
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
                EffectiveJoin: not BoundaryJoinType.Cut,
                CarryAudio: true,
            } boundary)
        {
            window = BoundaryOverlaps.EffectiveOverlapFrames(boundary);
            return window > 0;
        }
        window = 0;
        return false;
    }

    public bool TryGetReferenceContinueInput(
        int fromClipId,
        out int windowFrames,
        out double scale,
        out bool includeSoundtrack)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex >= 0
            && _effectiveBoundaries[boundaryIndex] is BoundaryPlan
            {
                EffectiveJoin: BoundaryJoinType.Continue,
                ContinueMode: ContinueBoundaryMode.Reference,
            } boundary)
        {
            windowFrames = boundary.ContinuityWindowFrames;
            scale = boundary.ReferenceScale;
            includeSoundtrack = boundary.ReferenceIncludeSoundtrack;
            return true;
        }
        windowFrames = 0;
        scale = 1;
        includeSoundtrack = true;
        return false;
    }

    public void DegradeToCut(int fromClipId)
    {
        int boundaryIndex = BoundaryIndex(fromClipId);
        if (boundaryIndex < 0
            || _effectiveBoundaries[boundaryIndex].EffectiveJoin == BoundaryJoinType.Cut)
        {
            return;
        }
        _effectiveBoundaries[boundaryIndex] =
            BoundaryOverlaps.DegradeToCut(_effectiveBoundaries[boundaryIndex]);
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
        List<DecodedClipArtifact> publishedOutputs = [clipOutputs[0]];
        List<BoundaryPlan> publishedBoundaries = [];
        for (int clipIndex = 1; clipIndex < clipOutputs.Count; clipIndex++)
        {
            ClipPlan clip = _plan.Clips[clipIndex];
            if (StringUtils.Equals(clip.InitVideo?.Source, MediaSource.PreviousClip))
            {
                publishedOutputs[^1] = clipOutputs[clipIndex];
                continue;
            }
            publishedOutputs.Add(clipOutputs[clipIndex]);
            publishedBoundaries.Add(_effectiveBoundaries[clipIndex - 1]);
        }
        if (publishedOutputs.Count == 1)
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
            return RuntimeArtifact.FromDecoded(_generator, bridge, publishedOutputs[0]);
        }
        return _merger.Merge(publishedOutputs, publishedBoundaries);
    }

    private int BoundaryIndex(int fromClipId) =>
        _effectiveBoundaries.FindIndex(boundary => boundary.FromClipId == fromClipId);
}
