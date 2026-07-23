using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Execution;

/// <summary>
/// Semantic provenance for graph-bound media. This is deliberately separate from the pure
/// planning model: plans describe what should happen, while runtime artifacts point at graph
/// outputs that already exist.
/// </summary>
internal enum ArtifactOrigin
{
    HostRoot,
    GlobalRefineVideo,
    SourceVideo,
    StageOutput,
    ClipAssembly
}

internal enum ArtifactVaeDisposition
{
    PreserveHost,
    Replace
}

/// <summary>
/// A typed media value at an orchestration boundary. <see cref="MediaRef"/> remains the single
/// representation of graph media; this envelope adds only VAE ownership and provenance.
/// </summary>
internal sealed record RuntimeArtifact(
    MediaRef Media,
    MediaRef Vae,
    ArtifactOrigin Origin,
    ArtifactVaeDisposition VaeDisposition = ArtifactVaeDisposition.Replace)
{
    public bool HasMedia => Media?.Output is not null;

    public RuntimeArtifact Copy(ArtifactOrigin? origin = null) =>
        new(Media?.Clone(), Vae?.Clone(), origin ?? Origin, VaeDisposition);

    /// <summary>Copies the graph-bound refs so changing metadata on one orchestration value cannot
    /// accidentally mutate another value that points at the same mutable <see cref="MediaRef"/>.</summary>
    public RuntimeArtifact WithOrigin(ArtifactOrigin origin) => Copy(origin);

    public static RuntimeArtifact Capture(
        WorkflowGenerator generator,
        WorkflowBridge bridge,
        ArtifactOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(bridge);
        return new RuntimeArtifact(
            MediaRef.FromWGNodeData(generator.CurrentMedia, bridge),
            MediaRef.FromWGNodeData(generator.CurrentVae, bridge),
            origin,
            ArtifactVaeDisposition.Replace);
    }

    /// <summary>
    /// Publishes the artifact back to the host compatibility surface. Internal orchestration should
    /// pass <see cref="RuntimeArtifact"/> values and call this only at explicit adapter boundaries.
    /// </summary>
    public void PublishTo(WorkflowGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        generator.CurrentMedia = Media?.ToWGNodeData(generator);
        if (VaeDisposition == ArtifactVaeDisposition.Replace)
        {
            generator.CurrentVae = Vae?.ToWGNodeData(generator);
        }
    }
}
