using ComfyTyped.Core;
using ComfyTyped.Generated;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>One geometry every clip shares once the timeline has been conformed.</summary>
internal sealed record TimelineGeometry(int Width, int Height, int FramesPerSecond)
{
    internal bool Matches(DecodedClipArtifact clip) =>
        clip.Width == Width && clip.Height == Height && clip.FramesPerSecond == FramesPerSecond;

    /// <summary>Frame count of <paramref name="frames"/> source frames after an fps resample,
    /// using SwarmVideoResampleFPS's own rounding so the graph and the plan agree.</summary>
    internal int ConformFrames(int frames, int sourceFramesPerSecond) =>
        sourceFramesPerSecond == FramesPerSecond
            ? frames
            : Math.Max(1, (int)Math.Round(
                frames / (double)sourceFramesPerSecond * FramesPerSecond));
}

/// <summary>
/// Conforms decoded clips to one geometry before overlap or concatenation.
/// </summary>
internal static class TimelineGeometryConform
{
    internal sealed record ConformResult(
        TimelineGeometry Target,
        IReadOnlyList<DecodedClipArtifact> Clips,
        IReadOnlyList<INodeOutput> VideoOutputs,
        IReadOnlyList<BoundaryPlan> Boundaries,
        IReadOnlyList<PlanDiagnostic> Diagnostics);

    private static TimelineGeometry ResolveTarget(IReadOnlyList<DecodedClipArtifact> clips) =>
        new(
            clips.Min(clip => clip.Width),
            clips.Min(clip => clip.Height),
            clips.Min(clip => clip.FramesPerSecond));

    /// <summary>
    /// Scales and resamples clips to the timeline minimum. Aspect-ratio changes produce a warning
    /// before width and height are conformed independently.
    /// </summary>
    internal static ConformResult Apply(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> videoOutputs,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(videoOutputs);
        TimelineGeometry target = ResolveTarget(clips);
        IReadOnlyList<BoundaryPlan> sourceBoundaries = boundaries ?? [];
        List<PlanDiagnostic> diagnostics = [
            .. clips
                .Where(clip => clip.Width * target.Height != clip.Height * target.Width)
                .Select(clip => new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "timeline-aspect-mismatch",
                    $"clip {clip.ClipId} finished at {clip.Width}x{clip.Height}, whose aspect ratio "
                    + $"differs from the timeline's {target.Width}x{target.Height}; conforming it "
                    + "would distort the image",
                    clip.ClipId))
        ];
        if (clips.All(target.Matches))
        {
            return new(target, clips, videoOutputs, sourceBoundaries, diagnostics);
        }

        List<DecodedClipArtifact> conformedClips = new(clips.Count);
        List<INodeOutput> conformedOutputs = new(clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            DecodedClipArtifact clip = clips[i];
            if (target.Matches(clip))
            {
                conformedClips.Add(clip);
                conformedOutputs.Add(videoOutputs[i]);
                continue;
            }
            diagnostics.Add(new PlanDiagnostic(
                PlanDiagnosticSeverity.Warning,
                "timeline-geometry-conformed",
                $"clip {clip.ClipId} finished at {clip.Width}x{clip.Height}@"
                + $"{clip.FramesPerSecond} and was conformed to {target.Width}x{target.Height}@"
                + $"{target.FramesPerSecond} for timeline assembly",
                clip.ClipId));
            INodeOutput conformed = Conform(bridge, videoOutputs[i], clip, target);
            conformedOutputs.Add(conformed);
            conformedClips.Add(clip with
            {
                Video = DecodedOutputHandle.From(conformed, DecodedMediaKind.Video),
                Width = target.Width,
                Height = target.Height,
                FramesPerSecond = target.FramesPerSecond,
                Frames = target.ConformFrames(clip.Frames, clip.FramesPerSecond),
            });
        }
        return new(
            target,
            conformedClips,
            conformedOutputs,
            ConformBoundaries(clips, sourceBoundaries, target),
            diagnostics);
    }

    private static INodeOutput Conform(
        WorkflowBridge bridge,
        INodeOutput source,
        DecodedClipArtifact clip,
        TimelineGeometry target)
    {
        INodeOutput conformed = source;
        if (clip.FramesPerSecond != target.FramesPerSecond)
        {
            SwarmVideoResampleFPSNode resample = bridge.AddNode(new SwarmVideoResampleFPSNode().With(
                FpsIn: (double)clip.FramesPerSecond,
                FpsOut: (double)target.FramesPerSecond,
                Method: "linear"));
            resample.ImagesInput.ConnectToUntyped(conformed);
            conformed = resample.Images;
        }
        if (clip.Width != target.Width || clip.Height != target.Height)
        {
            conformed = ImageScaleReuse.Create(
                bridge,
                WorkflowBridge.ToPath(conformed),
                target.Width,
                target.Height,
                crop: "disabled").IMAGE;
        }
        return conformed;
    }

    /// <summary>
    /// Re-expresses overlap durations on the conformed frame grid.
    /// </summary>
    private static IReadOnlyList<BoundaryPlan> ConformBoundaries(
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<BoundaryPlan> boundaries,
        TimelineGeometry target)
    {
        if (boundaries.Count != clips.Count - 1
            || clips.All(clip => clip.FramesPerSecond == target.FramesPerSecond))
        {
            return boundaries;
        }
        return Array.AsReadOnly(boundaries.Select((boundary, index) =>
        {
            int incomingFps = clips[index + 1].FramesPerSecond;
            return incomingFps == target.FramesPerSecond
                ? boundary
                : boundary with
                {
                    OverlapFrames = boundary.OverlapFrames <= 0
                        ? boundary.OverlapFrames
                        : target.ConformFrames(boundary.OverlapFrames, incomingFps),
                    ContinuityWindowFrames = boundary.ContinuityWindowFrames <= 0
                        ? boundary.ContinuityWindowFrames
                        : target.ConformFrames(boundary.ContinuityWindowFrames, incomingFps),
                };
        }).ToArray());
    }
}
