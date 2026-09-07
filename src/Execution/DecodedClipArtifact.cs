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

/// <summary>A bare graph-output pointer: no model compatibility, VAE, latent, or nested host
/// media object.</summary>
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

    /// <summary>Rebuilds an audio handle from a graph path a mixer produced outside the bridge.</summary>
    internal static DecodedOutputHandle FromPath(JArray path) =>
        path is { Count: 2 }
            ? new((string)path[0], (int)path[1], DecodedMediaKind.Audio)
            : throw Invariant.Failure(
                "decoded audio path is not a node output.");

    internal INodeOutput Resolve(WorkflowBridge bridge) =>
        bridge.ResolvePath(new JArray(NodeId, SlotIndex));

    internal JArray ToPath() => new(NodeId, SlotIndex);
}

/// <summary>
/// The architecture-neutral handoff from one completed clip to the timeline merge.
/// </summary>
internal sealed record DecodedClipArtifact(
    DecodedOutputHandle Video,
    DecodedOutputHandle Audio,
    int Width,
    int Height,
    int FramesPerSecond,
    int Frames,
    ArchitectureId ArchitectureId,
    int ClipId)
{
    internal void ValidateDecoded()
    {
        bool validVideo = Video?.Kind == DecodedMediaKind.Video
            && Width > 0
            && Height > 0
            && FramesPerSecond > 0
            && Frames > 0;
        bool validAudioOnly = Video is null
            && Audio?.Kind == DecodedMediaKind.Audio;
        if ((!validVideo && !validAudioOnly)
            || (Audio is not null && Audio.Kind != DecodedMediaKind.Audio))
        {
            throw Invariant.Failure(
                $"Clip {ClipId} returned an invalid decoded media artifact.");
        }
    }

    internal WGNodeData ToHostMedia(WorkflowGenerator g)
    {
        return Video is null ? ToAudioHostMedia(g) : ToVideoHostMedia(g);
    }

    internal WGNodeData ToVideoHostMedia(WorkflowGenerator g)
    {
        if (Video is null)
        {
            return null;
        }
        WGNodeData media = new(Video.ToPath(), g, WGNodeData.DT_VIDEO, null)
        {
            Width = Width,
            Height = Height,
            Frames = Frames,
            FPS = FramesPerSecond,
        };
        if (Audio is not null)
        {
            media.AttachedAudio = new(Audio.ToPath(), g, WGNodeData.DT_AUDIO, null);
        }
        return media;
    }

    internal WGNodeData ToAudioHostMedia(WorkflowGenerator g)
    {
        DecodedOutputHandle audio = Audio;
        if (audio is null)
        {
            return null;
        }
        return new(audio.ToPath(), g, WGNodeData.DT_AUDIO, null);
    }

    internal static DecodedClipArtifact FromRuntime(RuntimeArtifact artifact, ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(clip);
        if (!artifact.HasMedia)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} did not produce decoded video media.");
        }
        if (artifact.Media.DataType == WGNodeData.DT_AUDIO)
        {
            return new(
                null,
                DecodedOutputHandle.From(artifact.Media.Output, DecodedMediaKind.Audio),
                0,
                0,
                0,
                0,
                clip.Architecture?.Id ?? throw Invariant.Failure(
                    $"Clip {clip.ClipId} has no resolved architecture identity."),
                clip.ClipId);
        }
        if (artifact.Media.DataType != WGNodeData.DT_VIDEO)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} did not produce decoded video media; "
                + $"received '{artifact.Media.DataType ?? "unknown"}'.");
        }
        if (artifact.Media.AttachedAudio is MediaRef attachedAudio
            && attachedAudio.DataType != WGNodeData.DT_AUDIO)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} did not produce decoded attached audio; "
                + $"received '{attachedAudio.DataType ?? "unknown"}'.");
        }
        if (artifact.Media.Width is not > 0
            || artifact.Media.Height is not > 0
            || artifact.Media.Frames is not > 0
            || artifact.Media.FPS?.Type != JTokenType.Integer
            || artifact.Media.FPS.Value<int>() <= 0)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} decoded media is missing literal dimensions, fps, or frames.");
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
            clip.Architecture?.Id ?? throw Invariant.Failure(
                $"Clip {clip.ClipId} has no resolved architecture identity."),
            clip.ClipId);
        return decoded;
    }
}
