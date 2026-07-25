using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

/// <summary>An opaque architecture-owned clip compilation attached to the generic clip plan.</summary>
internal interface IArchitectureClipPayload
{
    ArchitectureId ArchitectureId { get; }
}

/// <summary>
/// A clip payload that can say, before anything runs, what dimensions its authored stage chain will
/// finish at. Timeline planning uses it to warn about conforming ahead of generation.
/// </summary>
internal interface IArchitectureClipGeometryProjection
{
    (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height);
}

/// <summary>
/// Opaque architecture-owned stage instruction. Common planning and timeline code only carries
/// this value to the selected architecture runtime.
/// </summary>
internal interface IArchitectureStagePayload
{
    ArchitectureId ArchitectureId { get; }
}

internal interface IArchitectureStagePayloadSource
{
    IArchitectureStagePayload GetStagePayload(int rawStageIndex);
}

internal sealed record ArchitectureClipCompilation(
    IArchitectureClipPayload Payload,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

/// <summary>One architecture session executes clips from only its own architecture.</summary>
internal interface IVideoGenerationSession : IDisposable
{
    ArchitectureId ArchitectureId { get; }

    ArchitectureClipExecutionRequest CreateExecutionRequest(
        ArchitectureClipRuntimeContext context);

    DecodedClipArtifact Execute(ArchitectureClipExecutionRequest request);
}

/// <summary>
/// Architecture-neutral timeline state supplied by the common runner. Each session projects this
/// into its own private runtime payload before execution.
/// </summary>
internal sealed record ArchitectureClipRuntimeContext(
    ClipPlan Clip,
    VideoExecutionPlan Plan,
    int ClipIndex,
    bool ParallelMultiClip,
    bool HasPreviousTimelineClip,
    ClipPlan PreviousClip,
    DecodedClipArtifact PreviousClipOutput,
    AudioRuntimeSources AudioSources,
    TimelineAssemblySession Assembly,
    RootExecutionPolicy RootPolicy)
{
    /// <summary>
    /// The prior timeline clip's decoded output regardless of boundary mode. This is contextual
    /// media only; <see cref="PreviousClipOutput"/> remains restricted to non-cut continuity.
    /// </summary>
    internal DecodedClipArtifact PreviousTimelineClipOutput { get; init; }
}

internal sealed record ArchitectureClipExecutionRequest(
    ClipPlan Clip,
    int ClipIndex,
    object RuntimePayload);

/// <summary>Architecture-owned graph construction for non-cut boundaries.</summary>
internal interface IArchitectureBoundaryAssembler
{
    ArchitectureId ArchitectureId { get; }

    INodeOutput MergeOverlaps(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> videoOutputs,
        BoundaryOverlapPlan plan);
}

internal sealed record ArchitectureTimelinePreparationContext(
    VideoExecutionPlan Plan,
    AudioRuntimeSources AudioSources,
    RootExecutionPolicy RootPolicy)
{
    public bool OwnsGeneratedRoot { get; init; }
}

/// <summary>
/// Graph-free request preflight input. Everything an architecture is asked here must be resolvable
/// from the compiled plan and the host session alone, because no workflow mutation has happened yet.
/// </summary>
internal sealed record ArchitectureRequestPreflightContext(
    VideoExecutionPlan Plan);

internal sealed record ArchitectureTimelineSessionContext(
    VideoExecutionPlan Plan,
    AudioRuntimeSources AudioSources,
    RootExecutionPolicy RootPolicy,
    TimelineAssemblySession Assembly);

internal sealed record ArchitectureTimelineFinalizationContext(
    VideoExecutionPlan Plan,
    OutputPublication Publication);

internal enum ArchitectureTimelineFinalizerScope
{
    None,
    WholeTimelineExclusive,
}

/// <summary>Creates one timeline-scoped session for one architecture.</summary>
internal interface IArchitectureGenerationSessionFactory
{
    ArchitectureId ArchitectureId { get; }

    IArchitectureBoundaryAssembler BoundaryAssembler { get; }

    ArchitectureTimelineFinalizerScope FinalizerScope =>
        ArchitectureTimelineFinalizerScope.None;

    bool HasFinalizationWork(ArchitectureTimelineFinalizationContext context) => false;

    void PrepareTimeline(ArchitectureTimelinePreparationContext context);

    IVideoGenerationSession CreateSession(ArchitectureTimelineSessionContext context);

    void FinalizeTimeline(ArchitectureTimelineFinalizationContext context);
}

/// <summary>Lazy production registration; architecture dependencies are built only when selected.</summary>
internal interface IArchitectureGenerationSessionFactoryProvider
{
    ArchitectureId ArchitectureId { get; }

    /// <summary>
    /// Reports every dependency this architecture needs to execute the planned clips, before any
    /// workflow phase has mutated the graph. Returning diagnostics rather than throwing keeps the
    /// decision about what a diagnostic does with <see cref="Planning.PlanDiagnosticReporter"/>.
    /// </summary>
    IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context) => [];

    IArchitectureGenerationSessionFactory CreateFactory();
}

internal enum ArchitectureHostPhase
{
    CaptureControlNetPreprocessors,
    CaptureBaseReference,
    CaptureRefinerReference,
    CapturePreCoreMedia,
    DropCoreOutput,
    ApplyRootAudioMaskDimensions,
}

internal enum ArchitectureHostPhaseScope
{
    RootOwnerOnly,
    AllActiveArchitectures,
}

internal static class ArchitectureHostPhasePolicy
{
    internal static ArchitectureHostPhaseScope Scope(ArchitectureHostPhase phase) => phase switch
    {
        ArchitectureHostPhase.CaptureControlNetPreprocessors
            or ArchitectureHostPhase.CaptureBaseReference
            or ArchitectureHostPhase.CaptureRefinerReference =>
            ArchitectureHostPhaseScope.AllActiveArchitectures,
        ArchitectureHostPhase.CapturePreCoreMedia
            or ArchitectureHostPhase.DropCoreOutput
            or ArchitectureHostPhase.ApplyRootAudioMaskDimensions =>
                ArchitectureHostPhaseScope.RootOwnerOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}

internal sealed record ArchitectureHostPhaseContext(
    ArchitectureHostPhase Phase,
    ArchitectureHostPhaseScope Scope,
    VideoExecutionPlan Plan,
    ArchitectureId? RootOwnerArchitectureId);

internal interface IArchitectureHostPhaseParticipant
{
    void ExecuteHostPhase(ArchitectureHostPhaseContext context);
}

/// <summary>
/// Architecture-selected root-media sizing behavior used by host image-to-video callbacks and
/// architecture runtimes without exposing a concrete implementation through common orchestration.
/// </summary>
internal interface IArchitectureRootMediaResizer
{
    bool TryGetRootStageResolution(out int width, out int height);

    void ApplyConfiguredRootStageResolutionToCurrentMedia();

    void ApplyConfiguredRootStageResolutionToSurvivingRootMedia();

    void ApplyCurrentMediaResolution(int width, int height);

    void SetCurrentMediaDimensions(int width, int height);
}

internal interface IArchitectureRootMediaResizerProvider
{
    IArchitectureRootMediaResizer CreateRootMediaResizer();
}
