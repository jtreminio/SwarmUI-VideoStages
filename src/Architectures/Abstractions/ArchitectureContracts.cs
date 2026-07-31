using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

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
    DecodedOutput = 1 << 4,
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
    Hdr = 1 << 8,
    FrameReferences = 1 << 9,
}

internal enum RuleSupport
{
    Supported,
    Unsupported,
    Conditional,
}

internal enum RuleScope
{
    Architecture,
    ModelProfile,
    Clip,
    Stage,
    Boundary,
    Output,
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

internal enum RuleFailureSeverity
{
    Warning,
}

internal enum RuleFailureEffect
{
    DisableFeature,
}

internal sealed record MinimumActiveStagesRuleConstraints(
    int MinimumActiveStages,
    RuleFailureSeverity FailureSeverity,
    RuleFailureEffect FailureEffect) : RuleConstraints;

internal sealed record MinimumStageControlRuleConstraints(
    double ExclusiveMinimumControl) : RuleConstraints;

internal sealed record FixedFrameCountRuleConstraints(
    bool RequiresFixedFrameCount) : RuleConstraints;

internal enum ConditionalRuleFeature
{
    Retake,
    FrameReferences,
    Hdr,
}

internal sealed record MutuallyExclusiveRuleConstraints(
    IReadOnlyList<ConditionalRuleFeature> MutuallyExclusive) : RuleConstraints;

internal sealed record RequiredEntryModesRuleConstraints(
    IReadOnlyList<ArchitectureEntryMode> RequiresAnyEntryMode) : RuleConstraints;

internal sealed record UniformTimelineFeatureRuleConstraints(
    ConditionalRuleFeature UniformTimelineFeature,
    int MinimumTimelineClips) : RuleConstraints;

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
    UniformTimelineHdr,
}

internal sealed record RuleDecision(
    RuleSupport Support,
    string Code,
    string Reason,
    RuleScope Scope,
    string EntityId = null,
    RuleConstraints Constraints = null)
{
    public static RuleDecision Supported(
        string code,
        string reason,
        RuleScope scope,
        RuleConstraints constraints = null) =>
        new(RuleSupport.Supported, code, reason, scope, Constraints: constraints);

    public static RuleDecision Unsupported(string code, string reason, RuleScope scope) =>
        new(RuleSupport.Unsupported, code, reason, scope);

    public static RuleDecision Conditional(
        string code,
        string reason,
        RuleScope scope,
        RuleConstraints constraints = null) =>
        new(RuleSupport.Conditional, code, reason, scope, Constraints: constraints);

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

internal sealed record ArchitectureCapabilityDescriptor(
    ArchitectureCapability Architecture,
    ClipCapability Clip,
    StageCapability Stage);

/// <summary>
/// One typed boundary rule used for both catalog publication and backend planning.
/// </summary>
internal sealed record ArchitectureBoundaryModePolicy(
    RuleSupport Support,
    string Code,
    string Reason,
    int FrameStep,
    int MinFrames,
    int MaxFrames,
    int DefaultFrames,
    int ContinuityExtraFrames,
    bool TargetRequiresGeneratedEntry,
    bool TargetRequiresStage,
    bool TargetDisallowsInitialReference)
{
    internal RuleDecision ToRuleDecision() => Support switch
    {
        RuleSupport.Supported => RuleDecision.Supported(Code, Reason, RuleScope.Boundary),
        RuleSupport.Unsupported => RuleDecision.Unsupported(Code, Reason, RuleScope.Boundary),
        _ => RuleDecision.Conditional(
            Code,
            Reason,
            RuleScope.Boundary,
            new BoundaryRuleConstraints(
                FrameStep,
                MinFrames,
                MaxFrames,
                DefaultFrames,
                ContinuityExtraFrames,
                TargetRequiresGeneratedEntry,
                TargetRequiresStage,
                TargetDisallowsInitialReference)),
    };

    internal int NormalizeOverlap(int authoredFrames)
    {
        if (Support == RuleSupport.Unsupported)
        {
            return 0;
        }
        int step = Math.Max(1, FrameStep);
        int candidate = Math.Clamp(
            authoredFrames <= 0 ? DefaultFrames : authoredFrames,
            MinFrames,
            MaxFrames);
        return MinFrames + ((candidate - MinFrames) / step * step);
    }
}

/// <summary>
/// The single owner of an architecture's boundary behavior: catalog publication and plan
/// compilation both read the same typed modes, so the advertised rule and the compiled rule
/// cannot drift.
/// </summary>
internal interface IArchitectureBoundaryPolicy
{
    IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> Modes { get; }

    IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> PublishedRules { get; }
}

/// <summary>A boundary policy declared as modes; its published rules are derived from them.</summary>
internal sealed class ArchitectureBoundaryPolicy : IArchitectureBoundaryPolicy
{
    internal ArchitectureBoundaryPolicy(
        IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> modes)
    {
        Modes = modes;
        PublishedRules = modes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToRuleDecision());
    }

    public IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> Modes { get; }

    public IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> PublishedRules { get; }

    internal static ArchitectureBoundaryPolicy CutOnly(
        string codePrefix,
        string cutReason,
        string continueReason,
        string crossfadeReason) =>
        new(new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
        {
            [BoundaryExecutionMode.Cut] = Mode(
                RuleSupport.Supported,
                $"{codePrefix}.boundary.cut",
                cutReason),
            [BoundaryExecutionMode.Continue] = Mode(
                RuleSupport.Unsupported,
                $"{codePrefix}.boundary.continue.unsupported",
                continueReason),
            [BoundaryExecutionMode.Crossfade] = Mode(
                RuleSupport.Unsupported,
                $"{codePrefix}.boundary.crossfade.unsupported",
                crossfadeReason),
        });

    private static ArchitectureBoundaryModePolicy Mode(
        RuleSupport support,
        string code,
        string reason) =>
        new(
            support,
            code,
            reason,
            FrameStep: 1,
            MinFrames: 0,
            MaxFrames: 0,
            DefaultFrames: 0,
            ContinuityExtraFrames: 0,
            TargetRequiresGeneratedEntry: false,
            TargetRequiresStage: false,
            TargetDisallowsInitialReference: false);
}

/// <summary>
/// Optional architecture-owned interpretation of a host ControlNet source. Common audio planning
/// carries only the authored duration owner; it never infers IC-LoRA source semantics.
/// </summary>
internal interface IArchitectureControlNetSourcePlan
{
    int? ControlNetSourceIndex { get; }
}

internal sealed record VideoModelProfileDescriptor(
    ModelProfileId Id,
    string DisplayName,
    IReadOnlyList<ArchitectureEntryMode> EntryModes,
    IReadOnlyList<RuleDecision> Rules);

/// <summary>
/// Optional authored product features that an effective-request projector can
/// safely omit while preserving their authored values. Absence means block.
/// </summary>
internal enum UnsupportedAuthoringFeature
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
    Hdr,
    Upscale,
}

internal sealed record VideoArchitectureDescriptor(
    ArchitectureId Id,
    string DisplayName,
    ModelProfileId DefaultProfileId,
    IReadOnlyList<AudioSourceKind> AudioSourceKinds,
    IReadOnlyList<VideoModelProfileDescriptor> Profiles,
    ArchitectureCapabilityDescriptor Capabilities,
    IArchitectureBoundaryPolicy BoundaryPolicy)
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

    /// <summary>
    /// Overview/backward catalog projection only. Entry authorization belongs exclusively to the
    /// resolved model facts and must never consult this union.
    /// </summary>
    public IReadOnlyList<ArchitectureEntryMode> EntryModes =>
        Array.AsReadOnly(Profiles
            .SelectMany(profile => profile.EntryModes)
            .Distinct()
            .ToArray());

    public IReadOnlyList<RuleDecision> Rules { get; init; } = [];

}

internal sealed record ResolvedVideoModel(
    string ModelName,
    ArchitectureId ArchitectureId,
    ModelProfileId ModelProfileId,
    VideoArchitectureDescriptor Architecture)
{
    /// <summary>
    /// Temporal requirement of the resolved model handler. It currently defaults to the
    /// architecture contract, while remaining model-scoped so a future handler can narrow it
    /// without reintroducing global timeline policy.
    /// </summary>
    public int? HandlerFrameGridOverride { get; init; }

    public int FrameGrid =>
        HandlerFrameGridOverride ?? Architecture?.FrameGrid ?? 1;

    /// <summary>
    /// Test and compatibility adapters receive a deterministic synthetic identity. Production
    /// registry resolutions must replace it and set <see cref="HostFactsAuthoritative"/>.
    /// </summary>
    public string ModelClassId { get; init; } = ModelName;

    public string CompatibilityClassId { get; init; } = ArchitectureId.Value;

    public VideoModelEntryAbility EntryAbilities { get; init; } =
        VideoModelEntryPolicy.FromArchitectureLegacyAlias(Architecture);

    /// <summary>
    /// Optional complete capability set for this resolved handler/model path. Null inherits the
    /// architecture contract. A non-null value may only narrow that contract; the registry rejects
    /// model resolutions that attempt to add capabilities the owning architecture did not declare.
    /// </summary>
    public ArchitectureCapabilityDescriptor CapabilityNarrowing { get; init; }

    public ArchitectureCapabilityDescriptor EffectiveCapabilities =>
        CapabilityNarrowing ?? Architecture.Capabilities;

    /// <summary>
    /// Optional complete audio-source set for this resolved handler/model path. Like capability
    /// narrowing, null inherits and a non-null value may only remove architecture-declared kinds.
    /// </summary>
    public IReadOnlyList<AudioSourceKind> AudioSourceKindNarrowing { get; init; }

    public IReadOnlyList<AudioSourceKind> EffectiveAudioSourceKinds =>
        AudioSourceKindNarrowing ?? Architecture.AudioSourceKinds;

    /// <summary>
    /// Frame positions accepted by this model's native image-conditioning path. Values are
    /// stable wire names such as <c>first</c>, <c>last</c>, and <c>any</c>.
    /// </summary>
    public IReadOnlyList<string> ReferencePositions { get; init; } = [];

    /// <summary>
    /// Core-owned LoRA targeting fact from the resolved model compatibility.
    /// False means normal LoRAs must not become effective solely through their
    /// text-encoder weight.
    /// </summary>
    public bool LorasTargetTextEncoder { get; init; } = true;

    public bool HostFactsAuthoritative { get; init; }
}

internal static class VideoModelEntryPolicy
{
    /// <summary>
    /// Compatibility fallback for synthetic/test adapters that predate host model facts. Production
    /// registry resolutions must publish explicit abilities and authoritative host facts.
    /// </summary>
    internal static VideoModelEntryAbility FromArchitectureLegacyAlias(
        VideoArchitectureDescriptor architecture)
    {
        IReadOnlyList<ArchitectureEntryMode> entryModes = architecture?.EntryModes ?? [];
        return (entryModes.Contains(ArchitectureEntryMode.TextToVideo)
                ? VideoModelEntryAbility.TextToVideo
                : VideoModelEntryAbility.None)
            | (entryModes.Any(mode => mode is
                    ArchitectureEntryMode.ImageToVideo
                    or ArchitectureEntryMode.SourceVideo
                    or ArchitectureEntryMode.RefineVideo)
                ? VideoModelEntryAbility.ImageToVideo
                : VideoModelEntryAbility.None);
    }

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

/// <summary>
/// Specialized modules win model resolution over the generic host-video fallback. Ambiguity
/// remains an error within the winning tier so registration order never silently changes policy.
/// </summary>
internal enum ArchitectureResolutionTier
{
    Specialized,
    Fallback,
}

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

internal sealed record ArchitectureClipCompileContext(
    int Width,
    int Height,
    int FramesPerSecond,
    ArchitectureEntryMode EntryMode = ArchitectureEntryMode.ImageToVideo,
    bool HasPreviousClipOutput = false);

internal interface IArchitecturePlanValidator
{
    IReadOnlyList<PlanDiagnostic> ValidatePlan(
        IReadOnlyList<ClipPlan> architectureClips,
        IReadOnlyList<ClipPlan> timelineClips,
        RootPlan root);
}

internal interface IVideoArchitectureRegistry
{
    IReadOnlyList<VideoArchitectureDescriptor> Catalog { get; }

    IReadOnlyList<ResolvedVideoModel> ResolvedModels { get; }

    IVideoArchitectureModule GetModule(ArchitectureId architectureId);

    bool TryResolveModel(string modelName, out ResolvedVideoModel resolved);

    bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved);
}
