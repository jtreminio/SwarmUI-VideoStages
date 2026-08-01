using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;


// --- Identity: normalized value types used as keys everywhere below ---

internal readonly record struct ArchitectureId
{
    public ArchitectureId(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Architecture id cannot be empty.", nameof(value))
            : value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

internal readonly record struct ModelProfileId
{
    public ModelProfileId(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Model profile id cannot be empty.", nameof(value))
            : value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

// --- Feature vocabulary: what an architecture can ever do. Published to the frontend as a
// wire-name list; see ArchitectureFeatureVocabulary for the spellings. ---

internal enum ArchitectureEntryMode
{
    TextToVideo,
    ImageToVideo,
    InitVideo,
}

[Flags]
internal enum ArchitectureFeature
{
    None = 0,
    PromptRelay = 1 << 0,
    FrameReferences = 1 << 1,
    ReferenceFraming = 1 << 2,
    Retake = 1 << 3,
    ClipAudio = 1 << 4,
    AudioSegments = 1 << 5,
    AudioReuse = 1 << 6,
    AudioDerivedDuration = 1 << 7,
    ControlSignalDerivedDuration = 1 << 8,
    IcLora = 1 << 9,
}

// --- Rules: what an architecture allows in a given configuration, where a capability flag is
// too coarse. Published with typed constraints so both sides evaluate the same thresholds. ---

internal enum RuleSupport
{
    Supported,
    Unsupported,
    Conditional,
}

internal enum RuleScope
{
    Clip,
    Boundary,
}

internal sealed record BoundaryRuleConstraints(
    int FrameStep,
    int MinFrames,
    int MaxFrames,
    int DefaultFrames,
    int ContinuityExtraFrames,
    bool TargetRequiresGeneratedEntry,
    bool TargetRequiresStage,
    bool TargetDisallowsInitialReference);

/// <summary>
/// Every conditional rule the frontend evaluates. The wire spelling lives in
/// <see cref="ArchitectureFeatureVocabulary.ConditionalRuleCodes"/>; publishing a rule whose code is
/// not registered here fails the vocabulary coverage test.
/// </summary>
internal enum ConditionalRuleCodeId
{
    RetakeRequiresSource,
}

internal sealed record RuleDecision(
    RuleSupport Support,
    string Code,
    string Reason,
    RuleScope Scope,
    BoundaryRuleConstraints Constraints = null)
{
    public static RuleDecision Supported(
        string code,
        string reason,
        RuleScope scope,
        BoundaryRuleConstraints constraints = null) =>
        new(RuleSupport.Supported, code, reason, scope, constraints);

    public static RuleDecision Unsupported(string code, string reason, RuleScope scope) =>
        new(RuleSupport.Unsupported, code, reason, scope);

    public static RuleDecision Conditional(
        string code,
        string reason,
        RuleScope scope,
        BoundaryRuleConstraints constraints = null) =>
        new(RuleSupport.Conditional, code, reason, scope, constraints);
}

// --- Boundary policy: the one rule family the backend also executes, not just publishes. ---

/// <summary>
/// The single owner of an architecture's boundary behavior: catalog publication and plan
/// compilation read the same typed rules.
/// </summary>
internal sealed class ArchitectureBoundaryPolicy
{
    internal ArchitectureBoundaryPolicy(
        IReadOnlyDictionary<BoundaryJoinType, RuleDecision> rules)
    {
        Rules = new Dictionary<BoundaryJoinType, RuleDecision>(
            rules ?? throw new ArgumentNullException(nameof(rules)));
    }

    internal IReadOnlyDictionary<BoundaryJoinType, RuleDecision> Rules { get; }

    internal static ArchitectureBoundaryPolicy CutOnly(
        string codePrefix,
        string cutReason,
        string continueReason = "This architecture has no continuity path.",
        string crossfadeReason = "This architecture has no decoded transition path.") =>
        new(new Dictionary<BoundaryJoinType, RuleDecision>
        {
            [BoundaryJoinType.Cut] = RuleDecision.Supported(
                $"{codePrefix}.boundary.cut",
                cutReason,
                RuleScope.Boundary),
            [BoundaryJoinType.Continue] = RuleDecision.Unsupported(
                $"{codePrefix}.boundary.continue.unsupported",
                continueReason,
                RuleScope.Boundary),
            [BoundaryJoinType.Crossfade] = RuleDecision.Unsupported(
                $"{codePrefix}.boundary.crossfade.unsupported",
                crossfadeReason,
                RuleScope.Boundary),
        });
}

// --- The catalog entry. Everything above is a part of it; everything below consumes it. ---

internal sealed record VideoArchitectureDescriptor(
    ArchitectureId Id,
    string DisplayName,
    IReadOnlyList<AudioSourceKind> AudioSourceKinds,
    IReadOnlyList<ArchitectureEntryMode> EntryModes,
    ArchitectureFeature Features,
    ArchitectureBoundaryPolicy BoundaryPolicy)
{
    /// <summary>
    /// The generated-frame grid shared by this architecture. It is an architecture/runtime fact,
    /// not authorization attached to a persisted profile alias.
    /// </summary>
    public int FrameGrid { get; init; } = 1;

    /// <summary>
    /// The effective stage guide selectors this architecture can execute. Fail closed so a new
    /// architecture must opt into every selector beyond the generated root.
    /// </summary>
    public StageGuideReferencePolicy StageGuideReferences { get; init; } =
        StageGuideReferencePolicy.GeneratedOnly;

    public IReadOnlyDictionary<BoundaryJoinType, RuleDecision> BoundaryRules =>
        BoundaryPolicy.Rules;

    public IReadOnlyList<RuleDecision> Rules { get; init; } = [];

}

// --- A host model bound to the architecture that claimed it ---

/// <param name="ReferencePositions">
/// Frame positions accepted by this model's native image-conditioning path. Values are
/// stable wire names such as <c>first</c>, <c>last</c>, and <c>any</c>.
/// </param>
/// <param name="LorasTargetTextEncoder">
/// Core-owned LoRA targeting fact from the resolved model compatibility.
/// False means normal LoRAs must not become effective solely through their
/// text-encoder weight.
/// </param>
internal sealed record ResolvedVideoModel(
    string ModelName,
    ModelProfileId ModelProfileId,
    VideoArchitectureDescriptor Architecture,
    string ModelClassId,
    string CompatibilityClassId,
    IReadOnlyList<string> ReferencePositions,
    bool LorasTargetTextEncoder)
{
    public ArchitectureId ArchitectureId => Architecture.Id;

    public int FrameGrid => Architecture.FrameGrid;
}

// --- Behavior contracts: backend-only, never serialized ---

internal sealed record ArchitectureClipCompileContext(
    int Width,
    int Height,
    int FramesPerSecond,
    ArchitectureEntryMode EntryMode = ArchitectureEntryMode.ImageToVideo,
    bool HasPreviousClipOutput = false);

internal interface IVideoArchitectureModule
{
    VideoArchitectureDescriptor Descriptor { get; }

    /// <summary>
    /// Specialized modules win model resolution over the generic host-video fallback. Ambiguity
    /// remains an error within the winning tier so registration order never silently changes
    /// policy.
    /// </summary>
    bool IsFallback => false;

    bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved);

    /// <summary>
    /// Performs only architecture-owned semantic validation and payload compilation.
    /// </summary>
    /// <remarks>
    /// Callers must first normalize the request, resolve every active stage through
    /// <see cref="VideoStages.Architectures.VideoArchitectureRegistry"/>, reject
    /// architecture-resolution diagnostics, and pass
    /// <see cref="VideoStages.Architectures.ArchitectureCapabilityValidator"/>. Implementations may
    /// therefore treat a
    /// missing active-stage resolution, mismatched architecture, or incompatible model as a caller
    /// contract violation instead of re-validating those facts.
    /// Architecture-private entry-mode semantics remain the module's responsibility.
    /// </remarks>
    ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context);
}

/// <summary>
/// Optional architecture-owned projection after model resolution and before
/// capability validation or workflow mutation. It runs once per selected
/// module and may replace only the clips listed in its context.
/// </summary>
internal interface IArchitectureEffectiveRequestProjector
{
    ArchitectureEffectiveRequestProjection ProjectEffectiveRequest(
        ArchitectureEffectiveRequestProjectionContext context);
}

/// <summary>
/// Optional architecture validation that runs after common compilation, when every clip carries
/// its compiled payload and its common audio/entry facts. Per-clip compilation cannot see those,
/// which is why this hook exists; it is not a timeline-wide pass.
/// </summary>
internal interface IArchitecturePlanValidator
{
    IReadOnlyList<PlanDiagnostic> ValidatePlan(IReadOnlyList<ClipPlan> architectureClips);
}

/// <summary>
/// Optional architecture-owned interpretation of a host ControlNet source. Common audio planning
/// carries only the authored duration owner; it never infers IC-LoRA source semantics.
/// </summary>
internal interface IArchitectureControlNetSourcePlan
{
    int? ControlNetSourceIndex { get; }
}

internal interface IVideoArchitectureRegistry
{
    IReadOnlyList<VideoArchitectureDescriptor> Catalog { get; }

    IReadOnlyList<ResolvedVideoModel> ResolvedModels { get; }

    IVideoArchitectureModule GetModule(ArchitectureId architectureId);

    bool TryResolveModel(string modelName, out ResolvedVideoModel resolved);

    bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved);
}
