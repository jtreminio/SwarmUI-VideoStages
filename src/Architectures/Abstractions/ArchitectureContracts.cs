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

internal enum ArchitectureAudioSourceKind
{
    Disabled,
    Native,
    Upload,
    ControlNet,
    AceStepFun,
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
internal enum ModelProfileCapability
{
    None = 0,
    SamplerSelection = 1 << 0,
    SchedulerSelection = 1 << 1,
    DimensionRules = 1 << 2,
    FrameRules = 1 << 3,
    NormalLora = 1 << 4,
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
    /// <summary>The clip can execute timeline audio spans projected onto it.</summary>
    AudioSegments = 1 << 6,
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

[Flags]
internal enum OutputCapability
{
    None = 0,
    Video = 1 << 0,
    AttachedAudio = 1 << 1,
    StandaloneAudio = 1 << 2,
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
}

internal sealed record ArchitectureCapabilityDescriptor(
    ArchitectureCapability Architecture,
    ClipCapability Clip,
    StageCapability Stage,
    OutputCapability Output);

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

internal interface IArchitectureBoundaryPolicy
{
    IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> Modes { get; }

    IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> PublishedRules { get; }
}

internal interface IArchitectureBoundaryPolicySource
{
    IArchitectureBoundaryPolicy BoundaryPolicy { get; }
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
    ModelProfileCapability Capabilities,
    IReadOnlyList<RuleDecision> Rules);

internal sealed record VideoArchitectureDescriptor(
    ArchitectureId Id,
    string DisplayName,
    ModelProfileId DefaultProfileId,
    IReadOnlyList<ArchitectureEntryMode> EntryModes,
    IReadOnlyList<ArchitectureAudioSourceKind> AudioSourceKinds,
    IReadOnlyList<VideoModelProfileDescriptor> Profiles,
    ArchitectureCapabilityDescriptor Capabilities,
    IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> BoundaryRules)
{
    public IReadOnlyList<ModelProfileId> ModelProfiles =>
        Array.AsReadOnly(Profiles.Select(profile => profile.Id).ToArray());

    public IReadOnlyList<RuleDecision> Rules { get; init; } = [];
}

internal sealed record ResolvedVideoModel(
    string ModelName,
    ArchitectureId ArchitectureId,
    ModelProfileId ModelProfileId,
    string HostCompatibilityId,
    VideoArchitectureDescriptor Architecture);

internal interface IVideoArchitectureModule
{
    VideoArchitectureDescriptor Descriptor { get; }

    bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved);

    ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context);
}

internal sealed record ArchitectureClipCompileContext(
    int Width,
    int Height,
    int FramesPerSecond,
    ArchitectureEntryMode EntryMode = ArchitectureEntryMode.ImageToVideo,
    bool HasPreviousClipOutput = false);

internal interface IArchitecturePlanValidator
{
    IReadOnlyList<VideoPlanDiagnostic> ValidatePlan(
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
