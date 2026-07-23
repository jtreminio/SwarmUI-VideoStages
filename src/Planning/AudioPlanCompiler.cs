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
        AudioPlanComponentResult<AudioVoiceReferencePlan> voiceReference = AudioVoiceReferencePlanCompiler.Compile(clip);
        AudioPlanComponentResult<AudioLengthPlan> length = AudioLengthPlanCompiler.Compile(clip, baseSource.Plan);
        AudioPlanComponentResult<AudioSegmentPlan> segments = AudioSegmentPlanCompiler.Compile(clip, baseSource.Plan);
        AudioPlanComponentResult<AudioReusePlan> reuse = AudioReusePlanCompiler.Compile(clip);

        return new(
            baseSource.Plan,
            voiceReference.Plan,
            length.Plan,
            segments.Plan,
            reuse.Plan,
            [.. baseSource.Diagnostics, .. voiceReference.Diagnostics, .. length.Diagnostics,
                .. segments.Diagnostics, .. reuse.Diagnostics]);
    }
}
