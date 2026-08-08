using SwarmUI.Text2Image;
using VideoStages.Authoring;

namespace VideoStages.Architectures.Abstractions;

internal sealed record ArchitectureClipCompileContext(
    int Width,
    int Height,
    int FramesPerSecond,
    ArchitectureEntryMode EntryMode,
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
/// Optional architecture-owned interpretation of a host ControlNet source. Common audio planning
/// carries only the authored duration owner; it never infers IC-LoRA source semantics.
/// </summary>
internal interface IArchitectureControlNetSourcePlan
{
    int? ControlNetSourceIndex { get; }
}
