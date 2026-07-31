using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// The one runtime interpretation of root ownership. All root handoff, text-to-video replacement,
/// native-audio suppression, and source-lead behavior derives from this policy rather than from
/// separate boolean predicates. Every fact it needs is already in the compiled plan, so runtime
/// callers cannot supply a second, divergent view of the same timeline.
/// </summary>
internal sealed class RootExecutionPolicy
{
    public RootExecutionPolicy(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan.Root ?? throw new ArgumentException("Plan has no root.", nameof(plan));
        ClipPlan first = plan.Clips.FirstOrDefault();
        FirstClipHasInitVideo = first?.HasInitVideo == true;
        HasInitVideoLeadWithGeneratedClips =
            FirstClipHasInitVideo && plan.Clips.Any(clip => !clip.HasInitVideo);
    }

    public RootPlan Plan { get; }

    public bool FirstClipHasInitVideo { get; }

    public bool HasInitVideoLeadWithGeneratedClips { get; }

    public bool InterceptsHostCore => Plan.CoreDisposition is not HostCoreDisposition.Keep;

    /// <summary>
    /// The current host media is the retained root handed into the first generated stage. A
    /// supplied refine source and initVideoClip first clip both provide their own stage input instead.
    /// </summary>
    public bool UsesStageHandoff => InterceptsHostCore
        && !FirstClipHasInitVideo;

    public bool DropsTextToVideoRootDonor => Plan.HostKind == HostRootKind.TextToVideoRoot
        && HasInitVideoLeadWithGeneratedClips;

    public bool ConformsSurvivingRootMedia =>
        Plan.Use == RootUse.GeneratedClipDonor;

    /// <summary>
    /// Only the first generated stage of a normal text-to-video timeline replaces the host text
    /// root. A global-refine source is explicitly not that case, even though its final output also
    /// replaces the root publication.
    /// </summary>
    public bool ReplacesTextToVideoRootStage(StagePlan stage, ClipPlan clip) =>
        Plan.HostKind == HostRootKind.TextToVideoRoot
        && Plan.Use == RootUse.Discard
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
