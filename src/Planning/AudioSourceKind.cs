namespace VideoStages.Planning;

/// <summary>
/// The one vocabulary for "where does this audio come from". Clip base audio, projected segments,
/// timeline tracks, and architecture capability declarations all name sources with this enum, so a
/// source can no longer mean different things on either side of a bridge function.
/// </summary>
internal enum AudioSourceKind
{
    /// <summary>The authored source string matched nothing known. Always a blocking diagnostic.</summary>
    Unknown,

    /// <summary>The architecture cannot produce audio for this source at all.</summary>
    Disabled,

    Native,
    Upload,
    ControlNet,
    AceStepFun,

    /// <summary>A timeline track whose media is resolved outside the clip audio vocabulary.</summary>
    External,
}

/// <summary>One parsed authored audio-source string.</summary>
internal sealed record AudioSourceSelection(
    AudioSourceKind Kind,
    string Raw,
    int? AceStepFunTrack);

/// <summary>The one parser for authored audio-source strings.</summary>
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
