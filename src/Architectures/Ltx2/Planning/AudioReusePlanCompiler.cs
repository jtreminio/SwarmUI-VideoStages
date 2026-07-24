using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiles the fixed generate-capture-reuse stage relationship.</summary>
internal static class AudioReusePlanCompiler
{
    internal static AudioPlanComponentResult<AudioReusePlan> Compile(ClipSpec clip)
    {
        int stageCount = clip.Stages?.Count ?? 0;
        bool eligible = clip.ReuseAudio
            && stageCount >= Ltx2ConditionalRulePolicySource.AudioReuseMinimumActiveStages;
        return new(
            new(clip.ReuseAudio, eligible, CaptureStageIndex: 1, ReuseFromStageIndex: 2),
            []);
    }
}
