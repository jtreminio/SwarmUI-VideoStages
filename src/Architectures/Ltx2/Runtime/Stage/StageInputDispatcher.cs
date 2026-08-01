namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// The named ways a stage gets its input. Exactly one applies to any stage, and
/// <see cref="StageInputDispatcher.Resolve"/> is the only place that decides which.
/// </summary>
internal enum StageInputCase
{
    /// <summary>The primary guide reference resolves to the stage input itself.</summary>
    PrimaryGuideIsStageInput,

    /// <summary>A "continue" boundary froze the previous clip's tail as this clip's opening guide.</summary>
    ContinuationTail,

    /// <summary>An authored ImageReference or frame reference supplies the primary guide.</summary>
    AuthoredGuideReference,

    /// <summary>This stage replaces the host text-to-video root and generates from scratch.</summary>
    TextToVideoRootReplacement,

    /// <summary>Other authored frame references already cover the guide, so nothing is re-injected.</summary>
    FrameReferencesOnly,

    /// <summary>
    /// A initVideoClip clip's own footage is the stage input; re-injecting it as an i2v guide would
    /// overwrite the noise mask of every frame it spans.
    /// </summary>
    InitVideoFootage,

    /// <summary>A defaulted stage past the opening one refines its incoming latent directly.</summary>
    IncomingLatentRefine,

    /// <summary>The previous stage's live latent is reusable, so no decode/re-encode round trip.</summary>
    PriorStageLatentReuse,

    NoGuide,

    /// <summary>The default: the resolved guide media is re-injected for this stage.</summary>
    GuideReinjection,
}

/// <summary>
/// The graph-derived facts the dispatch needs. They are gathered once by the stage runner so the
/// decision itself stays a pure, testable switch.
/// </summary>
internal readonly record struct StageInputFacts(
    bool HasPrimaryGuide,
    bool PrimaryGuideIsStageInput,
    bool IsContinuationTail,
    bool HasOtherFrameReferences,
    bool ReplacesTextToVideoRoot,
    bool InitVideoFootageIsStageInput,
    bool RefinesIncomingLatent,
    bool PriorStageLatentIsReusable,
    bool HasGuide);

internal static class StageInputDispatcher
{
    internal static StageInputCase Resolve(StageInputFacts facts) => facts switch
    {
        { HasPrimaryGuide: true, PrimaryGuideIsStageInput: true } =>
            StageInputCase.PrimaryGuideIsStageInput,
        { HasPrimaryGuide: true, IsContinuationTail: true } => StageInputCase.ContinuationTail,
        { HasPrimaryGuide: true } => StageInputCase.AuthoredGuideReference,
        { ReplacesTextToVideoRoot: true } => StageInputCase.TextToVideoRootReplacement,
        { HasOtherFrameReferences: true } => StageInputCase.FrameReferencesOnly,
        { InitVideoFootageIsStageInput: true } => StageInputCase.InitVideoFootage,
        { RefinesIncomingLatent: true } => StageInputCase.IncomingLatentRefine,
        { PriorStageLatentIsReusable: true } => StageInputCase.PriorStageLatentReuse,
        { HasGuide: false } => StageInputCase.NoGuide,
        _ => StageInputCase.GuideReinjection,
    };

    /// <summary>Whether the case must not have a guide re-injected.</summary>
    internal static bool SkipsGuideReinjection(StageInputCase inputCase) => inputCase
        is StageInputCase.TextToVideoRootReplacement
        or StageInputCase.FrameReferencesOnly
        or StageInputCase.InitVideoFootage
        or StageInputCase.IncomingLatentRefine
        or StageInputCase.PriorStageLatentReuse
        or StageInputCase.NoGuide;
}
