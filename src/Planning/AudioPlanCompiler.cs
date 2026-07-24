using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Converts parsed clip configuration into immutable audio policy. It intentionally does not inspect
/// the workflow: whether a native track, an AceStepFun decode, or a ControlNet capture exists is a
/// runtime artifact-resolution concern, not a planning concern.
/// </summary>
internal static class AudioPlanCompiler
{
    public static AudioPlan Compile(ClipSpec clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        AudioPlanComponentResult<AudioBaseSourcePlan> baseSource = AudioBaseSourcePlanCompiler.Compile(clip);
        AudioPlanComponentResult<AudioLengthPlan> length = AudioLengthPlanCompiler.Compile(clip, baseSource.Plan);

        // Segments are no longer authored per clip: the root timeline audio tracks are projected
        // onto each clip after the plan exists (see TimelineAudioSegmentPlanProjector).
        return new(
            baseSource.Plan,
            length.Plan,
            new(ImmutableArray<AudioSegmentItemPlan>.Empty),
            [.. baseSource.Diagnostics, .. length.Diagnostics]);
    }
}
