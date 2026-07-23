using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Execution;

internal enum DecodedMediaKind
{
    Video,
    Audio,
}

/// <summary>
/// A narrow graph-output identity. It carries no model compatibility, VAE, latent, or nested host
/// media object and is resolved only when the generic timeline assembler builds its final graph.
/// </summary>
internal sealed record DecodedOutputHandle(
    string NodeId,
    int SlotIndex,
    DecodedMediaKind Kind)
{
    internal static DecodedOutputHandle From(INodeOutput output, DecodedMediaKind kind)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new(output.Node.Id, output.SlotIndex, kind);
    }

    internal INodeOutput Resolve(WorkflowBridge bridge) =>
        bridge.ResolvePath(new JArray(NodeId, SlotIndex));

    internal JArray ToPath() => new(NodeId, SlotIndex);
}

/// <summary>
/// The architecture-neutral handoff from one completed clip to timeline assembly.
/// </summary>
internal sealed record DecodedClipArtifact(
    DecodedOutputHandle Video,
    DecodedOutputHandle Audio,
    int Width,
    int Height,
    int FramesPerSecond,
    int Frames,
    ArchitectureId ArchitectureId,
    int ClipId,
    ArtifactOrigin Origin)
{
    internal bool HasVideo => Video is not null;

    internal void ValidateDecoded()
    {
        if (Video?.Kind != DecodedMediaKind.Video
            || (Audio is not null && Audio.Kind != DecodedMediaKind.Audio)
            || Width <= 0
            || Height <= 0
            || FramesPerSecond <= 0
            || Frames <= 0)
        {
            throw new InvalidOperationException(
                $"Clip {ClipId} returned an invalid decoded media artifact.");
        }
    }

    internal static DecodedClipArtifact FromRuntime(RuntimeArtifact artifact, ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(clip);
        if (!artifact.HasMedia)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} did not produce decoded video media.");
        }
        if (artifact.Media.DataType != WGNodeData.DT_VIDEO)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} did not produce decoded video media; "
                + $"received '{artifact.Media.DataType ?? "unknown"}'.");
        }
        if (artifact.Media.AttachedAudio is MediaRef attachedAudio
            && attachedAudio.DataType != WGNodeData.DT_AUDIO)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} did not produce decoded attached audio; "
                + $"received '{attachedAudio.DataType ?? "unknown"}'.");
        }
        if (artifact.Media.Width is not > 0
            || artifact.Media.Height is not > 0
            || artifact.Media.Frames is not > 0
            || artifact.Media.FPS?.Type != JTokenType.Integer
            || artifact.Media.FPS.Value<int>() <= 0)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} decoded media is missing literal dimensions, fps, or frames.");
        }
        DecodedClipArtifact decoded = new(
            DecodedOutputHandle.From(artifact.Media.Output, DecodedMediaKind.Video),
            artifact.Media.AttachedAudio?.Output is INodeOutput audio
                ? DecodedOutputHandle.From(audio, DecodedMediaKind.Audio)
                : null,
            artifact.Media.Width.Value,
            artifact.Media.Height.Value,
            artifact.Media.FPS.Value<int>(),
            artifact.Media.Frames.Value,
            clip.Architecture?.Id ?? throw new InvalidOperationException(
                $"Clip {clip.ClipId} has no resolved architecture identity."),
            clip.ClipId,
            artifact.Origin);
        decoded.ValidateDecoded();
        return decoded;
    }
}
