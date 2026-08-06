using VideoStages.Execution.Audio;
using VideoStages.Execution.Graph;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

/// <summary>An opaque architecture-owned clip compilation attached to the generic clip plan.</summary>
internal interface IArchitectureClipPayload
{
    ArchitectureId ArchitectureId { get; }

    /// <summary>
    /// What dimensions this clip's authored stage chain will finish at, before anything runs.
    /// Timeline planning uses it to warn about conforming ahead of generation. Payloads whose clips
    /// carry no dimension-changing stages inherit the identity projection.
    /// </summary>
    (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height) => (width, height);
}

/// <summary>
/// Architecture-owned stage instruction. Its architecture-private detail stays opaque to common
/// planning and timeline code, which carries the value to the selected architecture runtime and
/// reads only the required <see cref="Core"/> facet.
/// </summary>
internal interface IArchitectureStagePayload
{
    ArchitectureId ArchitectureId { get; }

    StageCorePlan Core { get; }

    /// <summary>
    /// This stage consumes the previous stage's noisy sampler output instead of starting a new
    /// refinement pass. The absence of this instruction retains ordinary stage behavior.
    /// </summary>
    bool ContinuesSamplingFromPreviousStage => false;
}

/// <summary>
/// <paramref name="StagePayloads"/> holds architecture-owned stage instructions keyed by authored
/// raw stage index. Common planning attaches them to generic stage plans without interpreting their
/// concrete types.
/// </summary>
internal sealed record ArchitectureClipCompilation(
    IArchitectureClipPayload Payload,
    IReadOnlyDictionary<int, IArchitectureStagePayload> StagePayloads,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

/// <summary>One architecture session executes clips from only its own architecture.</summary>
internal interface IVideoGenerationSession : IDisposable
{
    ArchitectureId ArchitectureId { get; }

    DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context);

    /// <summary>Sessions that own no execution scope need no cleanup.</summary>
    void IDisposable.Dispose()
    {
    }
}

/// <summary>
/// Architecture-neutral per-clip facts supplied by the common runner. Timeline-scoped state is
/// captured by each architecture session when the session is created. Previous clip fields are
/// restricted to same-architecture, non-cut continuity; previous timeline output remains available
/// as contextual media regardless of boundary mode.
/// </summary>
internal sealed record ArchitectureClipRuntimeContext(
    ClipPlan Clip,
    int ClipIndex,
    ClipPlan PreviousClip,
    DecodedClipArtifact PreviousClipOutput,
    DecodedClipArtifact PreviousTimelineClipOutput);

/// <summary>
/// Graph-free request preflight input. Everything an architecture is asked here must be resolvable
/// from the compiled plan and the host session alone, because no workflow mutation has happened yet.
/// </summary>
internal sealed record ArchitectureRequestPreflightContext(
    VideoExecutionPlan Plan,
    ArchitectureId? RootOwnerArchitectureId);

internal sealed record ArchitectureTimelineSessionContext(
    VideoExecutionPlan Plan,
    AudioRuntimeSources AudioSources,
    Timeline.Boundaries Boundaries)
{
    public bool OwnsGeneratedRoot { get; init; }

    /// <summary>Shared across every session: there is one host root, so one claimant.</summary>
    public HostRootAdoption RootAdoption { get; init; }
}

/// <summary>
/// Request-scoped production registration. One provider instance is created for each active
/// architecture after the immutable plan passes compilation, then reused sequentially for request
/// preflight, host phases, and timeline-session creation. Mutable timeline and clip state belongs
/// in the returned session, not in the provider.
/// </summary>
internal interface IArchitectureGenerationSessionProvider
{
    ArchitectureId ArchitectureId { get; }

    /// <summary>
    /// Reports every dependency this architecture needs to execute the planned clips, before any
    /// workflow phase has mutated the graph. Returning diagnostics rather than throwing keeps the
    /// decision about what a diagnostic does with <see cref="Planning.PlanDiagnosticReporter"/>.
    /// </summary>
    IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context) => [];

    void CaptureControlNetPreprocessors(bool ownsHostRoot) { }

    void CaptureBaseReference(VideoExecutionPlan plan) { }

    void CaptureRefinerReference(VideoExecutionPlan plan) { }

    void ApplyRootAudioMaskDimensions() { }

    IVideoGenerationSession CreateSession(ArchitectureTimelineSessionContext context);
}
