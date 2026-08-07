using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>
/// Shared vocabulary for clip and timeline audio origins.
/// </summary>
internal enum AudioSourceKind
{
    /// <summary>The authored source string matched nothing known.</summary>
    Unknown,

    Disabled,
    Native,
    Upload,
    ControlNet,
    AceStepFun,
}

internal static class AudioSourceKindPolicy
{
    /// <summary>
    /// Only authored external audio can set video duration; native audio is generated with video.
    /// </summary>
    internal static bool CanDriveClipDuration(AudioSourceKind kind) =>
        kind is AudioSourceKind.Upload
            or AudioSourceKind.ControlNet
            or AudioSourceKind.AceStepFun;

    /// <summary>
    /// Whether the clip's authored audio-derived length is usable. A request against a source that
    /// cannot supply one is warned about by the capability pass and normalized away here.
    /// </summary>
    internal static bool CanUseAudioDerivedLength(ClipSpec clip) =>
        clip.ClipLengthFromAudio
        && CanDriveClipDuration(AudioSource.Parse(clip.AudioSource).Kind);
}

internal sealed record AudioSource(
    AudioSourceKind Kind,
    string Raw,
    int? AceStepFunTrack)
{
    internal static AudioSource Parse(string raw)
    {
        string trimmed = (raw ?? MediaSource.Native).Trim();
        if (trimmed.Length == 0 || StringUtils.Equals(trimmed, MediaSource.Native))
        {
            return new(AudioSourceKind.Native, trimmed, null);
        }
        if (StringUtils.Equals(trimmed, MediaSource.Upload))
        {
            return new(AudioSourceKind.Upload, trimmed, null);
        }
        if (StringUtils.Equals(trimmed, MediaSource.ControlNet))
        {
            return new(AudioSourceKind.ControlNet, trimmed, null);
        }
        return MediaSource.TryParseAceStepFunIndex(trimmed, out int track)
            ? new(AudioSourceKind.AceStepFun, trimmed, track)
            : new(AudioSourceKind.Unknown, trimmed, null);
    }
}
