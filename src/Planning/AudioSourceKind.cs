namespace VideoStages.Planning;

/// <summary>
/// Shared vocabulary for clip and timeline audio origins.
/// </summary>
internal enum AudioSourceKind
{
    /// <summary>The authored source string matched nothing known.</summary>
    Unknown,

    /// <summary>Audio is disabled.</summary>
    Disabled,

    Native,
    Upload,
    ControlNet,
    AceStepFun,

    /// <summary>A timeline track whose media is resolved outside the clip audio vocabulary.</summary>
    External,
}

/// <summary>Shared source-kind prerequisites for architecture capability validation.</summary>
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
    /// Whether a clip's authored audio-derived duration is usable. An authored request against a
    /// source that cannot supply a length is warned about once by the capability pass and
    /// normalized away here, so the clip keeps its authored length everywhere downstream.
    /// </summary>
    internal static bool AudioOwnsClipDuration(ClipSpec clip) =>
        clip.ClipLengthFromAudio
        && CanDriveClipDuration(AudioSourceParser.Parse(clip.AudioSource).Kind);
}

/// <summary>A parsed authored audio-source string.</summary>
internal sealed record AudioSourceSelection(
    AudioSourceKind Kind,
    string Raw,
    int? AceStepFunTrack);

/// <summary>Parses authored audio-source strings.</summary>
internal static class AudioSourceParser
{
    internal static AudioSourceSelection Parse(string raw)
    {
        string trimmed = (raw ?? Constants.AudioSourceNative).Trim();
        if (trimmed.Length == 0 || StringUtils.Equals(trimmed, Constants.AudioSourceNative))
        {
            return new(AudioSourceKind.Native, trimmed, null);
        }
        if (StringUtils.Equals(trimmed, Constants.AudioSourceUpload))
        {
            return new(AudioSourceKind.Upload, trimmed, null);
        }
        if (StringUtils.Equals(trimmed, Constants.AudioSourceControlNet))
        {
            return new(AudioSourceKind.ControlNet, trimmed, null);
        }
        return AudioHandler.TryParseAceStepFunAudioSource(trimmed, out int track)
            ? new(AudioSourceKind.AceStepFun, trimmed, track)
            : new(AudioSourceKind.Unknown, trimmed, null);
    }
}
