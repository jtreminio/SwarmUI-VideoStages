using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Execution;

/// <summary>
/// A typed media value at an orchestration boundary. <see cref="MediaRef"/> remains the single
/// representation of graph media; this envelope adds only VAE ownership.
/// </summary>
internal sealed record RuntimeArtifact(
    MediaRef Media,
    MediaRef Vae)
{
    public bool HasMedia => Media?.Output is not null;

    public static RuntimeArtifact Capture(
        WorkflowGenerator generator,
        WorkflowBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(bridge);
        return new RuntimeArtifact(
            MediaRef.FromWGNodeData(generator.CurrentMedia, bridge),
            MediaRef.FromWGNodeData(generator.CurrentVae, bridge));
    }

    public static RuntimeArtifact FromDecoded(
        WorkflowGenerator generator,
        WorkflowBridge bridge,
        DecodedClipArtifact decoded)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(decoded);
        return new(
            MediaRef.FromWGNodeData(decoded.ToHostMedia(generator), bridge),
            MediaRef.FromWGNodeData(generator.CurrentVae, bridge));
    }

    /// <summary>
    /// Publishes the artifact back to the host compatibility surface. Internal orchestration should
    /// pass <see cref="RuntimeArtifact"/> values and call this only at explicit adapter boundaries.
    /// </summary>
    public void PublishTo(WorkflowGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        generator.CurrentMedia = Media?.ToWGNodeData(generator);
        generator.CurrentVae = Vae?.ToWGNodeData(generator);
    }
}
