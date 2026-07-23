using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Facts that are only known while a compiled root plan is being executed. They are deliberately
/// separate from <see cref="RootPlan"/>: source installation is a runtime action, while the plan
/// remains graph-free.
/// </summary>
internal sealed record RootExecutionFacts(
    bool HasInstalledRefineSource,
    bool FirstClipIsSourced,
    bool HasSourcedLeadWithGeneratedClips)
{
    public static RootExecutionFacts FromPlan(
        VideoExecutionPlan plan,
        bool hasInstalledRefineSource)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ClipPlan first = plan.Clips.FirstOrDefault();
        return new(
            hasInstalledRefineSource,
            first?.IsSourced == true,
            first?.IsSourced == true && plan.Clips.Any(clip => !clip.IsSourced));
    }
}

/// <summary>
/// The one runtime interpretation of root ownership. All root handoff, text-to-video replacement,
/// native-audio suppression, and source-lead behavior derives from this policy rather than from
/// separate boolean predicates.
/// </summary>
internal sealed class RootExecutionPolicy(RootPlan plan, RootExecutionFacts facts)
{
    public RootPlan Plan { get; } = plan ?? throw new ArgumentNullException(nameof(plan));

    public RootExecutionFacts Facts { get; } = facts ?? throw new ArgumentNullException(nameof(facts));

    public bool InterceptsHostCore => Plan.CoreDisposition is not HostCoreDisposition.Keep;

    /// <summary>
    /// The current host media is the retained root handed into the first generated stage. A
    /// supplied refine source and sourced first clip both provide their own stage input instead.
    /// </summary>
    public bool UsesStageHandoff => InterceptsHostCore
        && !Facts.HasInstalledRefineSource
        && !Facts.FirstClipIsSourced;

    public bool DropsTextToVideoRootDonor => Plan.HostKind == HostRootKind.TextToVideoRoot
        && Facts.HasSourcedLeadWithGeneratedClips;

    public bool ConformsSurvivingRootMedia => !Facts.HasInstalledRefineSource
        && !DropsTextToVideoRootDonor
        && Facts.HasSourcedLeadWithGeneratedClips;

    /// <summary>
    /// Only the first generated stage of a normal text-to-video timeline replaces the host text
    /// root. A global-refine source is explicitly not that case, even though its final output also
    /// replaces the root publication.
    /// </summary>
    public bool ReplacesTextToVideoRootStage(StagePlan stage, ClipPlan clip) =>
        Plan.HostKind == HostRootKind.TextToVideoRoot
        && Plan.Use == RootUse.Discard
        && !Facts.HasInstalledRefineSource
        && clip?.Input == ClipInputKind.EmptyLatent
        && stage?.Input == StageInputKind.EmptyLatent
        && stage.ClipStageIndex == 0;

    /// <summary>
    /// A stage may replace the text root while executing from another clip's source donor. Only a
    /// real root-stage handoff suppresses native audio; source-led timelines retain their donor
    /// audio until their own clip policy resolves it.
    /// </summary>
    public bool SuppressesNativeAudioForStage(StagePlan stage, ClipPlan clip) =>
        UsesStageHandoff && ReplacesTextToVideoRootStage(stage, clip);
}
