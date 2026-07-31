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

// --- Capability vocabulary: what an architecture can ever do. Published to the frontend as
// wire-name lists; see ArchitectureFeatureVocabulary for the spellings. ---

internal enum ArchitectureEntryMode
{
    TextToVideo,
    ImageToVideo,
    SourceVideo,
    RefineVideo,
}

[Flags]
internal enum VideoModelEntryAbility
{
    None = 0,
    TextToVideo = 1 << 0,
    ImageToVideo = 1 << 1,
}

[Flags]
internal enum ArchitectureCapability
{
    None = 0,
    GeneratedEntry = 1 << 0,
    SourcedEntry = 1 << 1,
    MultiStage = 1 << 2,
    NativeAudio = 1 << 3,
}

[Flags]
internal enum ClipCapability
{
    None = 0,
    SourceVideo = 1 << 0,
    Prompts = 1 << 1,
    PromptRelay = 1 << 2,
    References = 1 << 3,
    Retake = 1 << 4,
    AudioSources = 1 << 5,
    AudioSegments = 1 << 6,
    ReferenceFraming = 1 << 7,
    AudioReuse = 1 << 8,
    AudioDerivedDuration = 1 << 9,
    ControlSignalDerivedDuration = 1 << 10,
}

[Flags]
internal enum StageCapability
{
    None = 0,
    ImageInput = 1 << 0,
    VideoInput = 1 << 1,
    PixelUpscale = 1 << 2,
    ModelUpscale = 1 << 3,
    LatentUpscale = 1 << 4,
    LatentModelUpscale = 1 << 5,
    Lora = 1 << 6,
    IcLora = 1 << 7,
    FrameReferences = 1 << 8,
}

internal sealed record ArchitectureCapabilityDescriptor(
    ArchitectureCapability Architecture,
    ClipCapability Clip,
    StageCapability Stage);

/// <summary>
/// Authored product features an architecture may or may not support. Each entry's vocabulary
/// binding decides whether an effective-request projector can omit it while preserving the
/// authored value, or whether an unsupported feature blocks instead.
/// </summary>
internal enum AuthoringFeature
{
    MultiStage,
    SourceVideo,
    FrameReferences,
    ReferenceFraming,
    Retake,
    MajorPrompt,
    PromptRelay,
    ClipAudio,
    AudioReuse,
    AudioDerivedDuration,
    ControlSignalDerivedDuration,
    StageLoras,
    IcLora,
    Upscale,
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
    Stage,
    Boundary,
}

internal abstract record RuleConstraints;

internal sealed record BoundaryRuleConstraints(
    int FrameStep,
    int MinFrames,
    int MaxFrames,
    int DefaultFrames,
    int ContinuityExtraFrames,
    bool TargetRequiresGeneratedEntry,
    bool TargetRequiresStage,
    bool TargetDisallowsInitialReference) : RuleConstraints;

internal sealed record MinimumActiveStagesRuleConstraints(
    int MinimumActiveStages) : RuleConstraints;

internal sealed record MinimumStageControlRuleConstraints(
    double ExclusiveMinimumControl) : RuleConstraints;

internal enum ConditionalRuleFeature
{
    Retake,
    FrameReferences,
}

internal sealed record MutuallyExclusiveRuleConstraints(
    IReadOnlyList<ConditionalRuleFeature> MutuallyExclusive) : RuleConstraints;

internal sealed record RequiredEntryModesRuleConstraints(
    IReadOnlyList<ArchitectureEntryMode> RequiresAnyEntryMode) : RuleConstraints;

/// <summary>
/// Every conditional rule the frontend evaluates. The wire spelling lives in
/// <see cref="ArchitectureFeatureVocabulary.ConditionalRuleCodes"/>; publishing a rule whose code is
/// not registered here fails the vocabulary coverage test.
/// </summary>
internal enum ConditionalRuleCodeId
{
    AudioReuseRequiresStages,
    NormalLoraRequiresSamplingStage,
    PromptRelayRequiresFixedLength,
    RetakeExcludesReferences,
    RetakeRequiresSource,
}

internal sealed record RuleDecision(
    RuleSupport Support,
    string Code,
    string Reason,
    RuleScope Scope,
    RuleConstraints Constraints = null)
{
    public static RuleDecision Supported(
        string code,
        string reason,
        RuleScope scope,
        RuleConstraints constraints = null) =>
        new(RuleSupport.Supported, code, reason, scope, constraints);

    public static RuleDecision Unsupported(string code, string reason, RuleScope scope) =>
        new(RuleSupport.Unsupported, code, reason, scope);

    public static RuleDecision Conditional(
        string code,
        string reason,
        RuleScope scope,
        RuleConstraints constraints = null) =>
        new(RuleSupport.Conditional, code, reason, scope, constraints);

    /// <summary>
    /// The published constraints, typed. Evaluators read their thresholds from here so a rule's
    /// numbers exist once instead of being restated as literals next to the check.
    /// </summary>
    public TConstraints Require<TConstraints>()
        where TConstraints : RuleConstraints =>
        Constraints as TConstraints
        ?? throw new InvalidOperationException(
            $"Rule '{Code}' does not publish {typeof(TConstraints).Name}.");
}

// --- Boundary policy: the one rule family the backend also executes, not just publishes.
// The executable mode projects to the published rule so the two cannot drift. ---

/// <summary>
/// One typed boundary rule used for both catalog publication and backend planning.
/// </summary>
internal sealed record ArchitectureBoundaryModePolicy
{
    private ArchitectureBoundaryModePolicy(
        RuleSupport support,
        string code,
        string reason,
        BoundaryRuleConstraints constraints)
    {
        Support = support;
        Code = RequireText(code, nameof(code));
        Reason = RequireText(reason, nameof(reason));
        Constraints = constraints;
    }

    internal RuleSupport Support { get; }

    internal string Code { get; }

    internal string Reason { get; }

    /// <summary>
    /// The thresholds a conditional mode publishes and compiles against; null for the
    /// unconditional modes, which have no numbers to agree on.
    /// </summary>
    internal BoundaryRuleConstraints Constraints { get; }

    internal static ArchitectureBoundaryModePolicy Supported(string code, string reason) =>
        new(RuleSupport.Supported, code, reason, null);

    internal static ArchitectureBoundaryModePolicy Unsupported(string code, string reason) =>
        new(RuleSupport.Unsupported, code, reason, null);

    internal static ArchitectureBoundaryModePolicy Conditional(
        string code,
        string reason,
        BoundaryRuleConstraints constraints) =>
        new(
            RuleSupport.Conditional,
            code,
            reason,
            constraints ?? throw new ArgumentNullException(nameof(constraints)));

    internal RuleDecision ToRuleDecision() => Support switch
    {
        RuleSupport.Supported => RuleDecision.Supported(Code, Reason, RuleScope.Boundary),
        RuleSupport.Unsupported => RuleDecision.Unsupported(Code, Reason, RuleScope.Boundary),
        _ => RuleDecision.Conditional(Code, Reason, RuleScope.Boundary, Constraints),
    };

    internal int NormalizeOverlap(int authoredFrames)
    {
        if (Support == RuleSupport.Unsupported || Constraints is null)
        {
            return 0;
        }
        int step = Math.Max(1, Constraints.FrameStep);
        int candidate = Math.Clamp(
            authoredFrames <= 0 ? Constraints.DefaultFrames : authoredFrames,
            Constraints.MinFrames,
            Constraints.MaxFrames);
        return Constraints.MinFrames + ((candidate - Constraints.MinFrames) / step * step);
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value;
}

/// <summary>
/// The single owner of an architecture's boundary behavior: catalog publication and plan
/// compilation both read the same typed modes, so the advertised rule and the compiled rule
/// cannot drift.
/// </summary>
internal sealed class ArchitectureBoundaryPolicy
{
    internal ArchitectureBoundaryPolicy(
        IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> modes)
    {
        Modes = new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>(
            modes ?? throw new ArgumentNullException(nameof(modes)));
        PublishedRules = Modes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToRuleDecision());
    }

    internal IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> Modes
    {
        get;
    }

    internal IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> PublishedRules { get; }

    internal static ArchitectureBoundaryPolicy CutOnly(
        string codePrefix,
        string cutReason,
        string continueReason,
        string crossfadeReason) =>
        new(new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
        {
            [BoundaryExecutionMode.Cut] = ArchitectureBoundaryModePolicy.Supported(
                $"{codePrefix}.boundary.cut",
                cutReason),
            [BoundaryExecutionMode.Continue] = ArchitectureBoundaryModePolicy.Unsupported(
                $"{codePrefix}.boundary.continue.unsupported",
                continueReason),
            [BoundaryExecutionMode.Crossfade] = ArchitectureBoundaryModePolicy.Unsupported(
                $"{codePrefix}.boundary.crossfade.unsupported",
                crossfadeReason),
        });
}

// --- The catalog entry. Everything above is a part of it; everything below consumes it. ---

internal sealed record VideoArchitectureDescriptor(
    ArchitectureId Id,
    string DisplayName,
    IReadOnlyList<AudioSourceKind> AudioSourceKinds,
    IReadOnlyList<ArchitectureEntryMode> EntryModes,
    ArchitectureCapabilityDescriptor Capabilities,
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

    /// <summary>The catalog projection of <see cref="BoundaryPolicy"/>; never a separate source.</summary>
    public IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> BoundaryRules =>
        BoundaryPolicy.PublishedRules;

    public IReadOnlyList<RuleDecision> Rules { get; init; } = [];

}

// --- A host model bound to the architecture that claimed it ---

internal sealed record ResolvedVideoModel
{
    internal ResolvedVideoModel(
        string modelName,
        ModelProfileId modelProfileId,
        VideoArchitectureDescriptor architecture,
        string modelClassId,
        string compatibilityClassId,
        VideoModelEntryAbility entryAbilities,
        IReadOnlyList<string> referencePositions,
        bool lorasTargetTextEncoder)
    {
        ModelName = RequireText(modelName, nameof(modelName));
        if (string.IsNullOrWhiteSpace(modelProfileId.Value))
        {
            throw new ArgumentException(
                "Model profile id cannot be empty.",
                nameof(modelProfileId));
        }
        ModelProfileId = modelProfileId;
        Architecture = architecture
            ?? throw new ArgumentNullException(nameof(architecture));
        ModelClassId = RequireText(modelClassId, nameof(modelClassId));
        CompatibilityClassId = RequireText(
            compatibilityClassId,
            nameof(compatibilityClassId));
        VideoModelEntryAbility knownAbilities =
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo;
        if (entryAbilities == VideoModelEntryAbility.None
            || (entryAbilities & ~knownAbilities) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entryAbilities),
                entryAbilities,
                "A resolved video model must declare at least one known entry ability.");
        }
        EntryAbilities = entryAbilities;
        ReferencePositions = Array.AsReadOnly(
            (referencePositions
                ?? throw new ArgumentNullException(nameof(referencePositions))).ToArray());
        LorasTargetTextEncoder = lorasTargetTextEncoder;
    }

    public string ModelName { get; }

    public ArchitectureId ArchitectureId => Architecture.Id;

    public ModelProfileId ModelProfileId { get; }

    public VideoArchitectureDescriptor Architecture { get; }

    public int FrameGrid => Architecture.FrameGrid;

    public string ModelClassId { get; }

    public string CompatibilityClassId { get; }

    public VideoModelEntryAbility EntryAbilities { get; }

    /// <summary>
    /// Frame positions accepted by this model's native image-conditioning path. Values are
    /// stable wire names such as <c>first</c>, <c>last</c>, and <c>any</c>.
    /// </summary>
    public IReadOnlyList<string> ReferencePositions { get; }

    /// <summary>
    /// Core-owned LoRA targeting fact from the resolved model compatibility.
    /// False means normal LoRAs must not become effective solely through their
    /// text-encoder weight.
    /// </summary>
    public bool LorasTargetTextEncoder { get; }

    internal ResolvedVideoModel WithArchitecture(
        VideoArchitectureDescriptor architecture) =>
        new(
            ModelName,
            ModelProfileId,
            architecture,
            ModelClassId,
            CompatibilityClassId,
            EntryAbilities,
            ReferencePositions,
            LorasTargetTextEncoder);

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }
        return value;
    }
}

internal static class VideoModelEntryPolicy
{
    internal static bool SupportsStageRole(
        ResolvedVideoModel model,
        int activeStageIndex,
        ArchitectureEntryMode rootEntryMode)
    {
        if (model is null)
        {
            return false;
        }
        VideoModelEntryAbility required = activeStageIndex == 0
            ? rootEntryMode == ArchitectureEntryMode.TextToVideo
                ? VideoModelEntryAbility.TextToVideo
                : VideoModelEntryAbility.ImageToVideo
            : VideoModelEntryAbility.ImageToVideo;
        return (model.EntryAbilities & required) == required;
    }
}

// --- Behavior contracts: backend-only, never serialized ---

/// <summary>
/// Specialized modules win model resolution over the generic host-video fallback. Ambiguity
/// remains an error within the winning tier so registration order never silently changes policy.
/// </summary>
internal enum ArchitectureResolutionTier
{
    Specialized,
    Fallback,
}

internal sealed record ArchitectureClipCompileContext(
    int Width,
    int Height,
    int FramesPerSecond,
    ArchitectureEntryMode EntryMode = ArchitectureEntryMode.ImageToVideo,
    bool HasPreviousClipOutput = false);

internal interface IVideoArchitectureModule
{
    VideoArchitectureDescriptor Descriptor { get; }

    ArchitectureResolutionTier ResolutionTier =>
        ArchitectureResolutionTier.Specialized;

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
    /// missing active-stage resolution, mismatched architecture, incompatible model, or missing
    /// model entry ability as a caller contract violation instead of re-validating those facts.
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
