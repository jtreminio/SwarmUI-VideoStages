using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Compiles the fixed generate-capture-reuse stage relationship.</summary>
internal static class AudioReusePlanCompiler
{
    private const string ReuseNeedsThreeStages = "audio.reuse.requires_three_stages";

    internal static AudioPlanComponentResult<AudioReusePlan> Compile(ClipSpec clip)
    {
        int stageCount = clip.Stages?.Count ?? 0;
        bool eligible = clip.ReuseAudio && stageCount >= 3;
        ImmutableArray<AudioPlanDiagnostic> diagnostics = clip.ReuseAudio && !eligible
            ? [new(
                ReuseNeedsThreeStages,
                "Audio reuse needs at least three active stages: generate, capture, then reuse.")]
            : [];
        return new(new(clip.ReuseAudio, eligible, CaptureStageIndex: 1, ReuseFromStageIndex: 2), diagnostics);
    }
}
