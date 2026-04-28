using SwarmUI.Utils;

namespace VideoStages;

internal static class ClipAudioWorkflowHelper
{
    internal enum ClipAudioSourceNormalization
    {
        CoordinatorField,
        StageSpec
    }

    internal static bool ShouldMatchVideoLengthForTryInjectAudio(
        string audioSource,
        bool clipLengthFromAudio,
        bool restrictLengthMatchToUploadOrAce)
    {
        if (restrictLengthMatchToUploadOrAce)
        {
            if (!clipLengthFromAudio)
            {
                return false;
            }
            if (StringUtils.Equals(audioSource, Constants.AudioSourceUpload))
            {
                return true;
            }
            return AudioStageDetector.TryParseAceStepFunAudioSource(audioSource, out _);
        }

        if (StringUtils.Equals(audioSource, Constants.AudioSourceUpload)
            || AudioStageDetector.TryParseAceStepFunAudioSource(audioSource, out _))
        {
            return clipLengthFromAudio;
        }
        return true;
    }

    internal static AudioStageDetector.Detection ResolveClipAudioDetection(
        int clipId,
        string audioSourceRaw,
        AudioStageDetector.Detection nativeFallback,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> clipAudios,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> uploadedAudios,
        bool suppressNativeFallback,
        ClipAudioSourceNormalization normalization)
    {
        string source;
        switch (normalization)
        {
            case ClipAudioSourceNormalization.CoordinatorField:
                source = audioSourceRaw?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(source))
                {
                    return suppressNativeFallback ? null : nativeFallback;
                }
                break;
            case ClipAudioSourceNormalization.StageSpec:
                source = (audioSourceRaw ?? Constants.AudioSourceNative).Trim();
                if (string.IsNullOrWhiteSpace(source))
                {
                    return null;
                }
                break;
            default:
                Logs.Error(nameof(normalization));
                return null;
        }

        if (StringUtils.Equals(source, Constants.AudioSourceUpload))
        {
            if (uploadedAudios is null)
            {
                return null;
            }
            return uploadedAudios.TryGetValue(clipId, out AudioStageDetector.Detection uploaded)
                ? uploaded
                : null;
        }
        if (AudioStageDetector.TryParseAceStepFunAudioSource(source, out _))
        {
            if (clipAudios is null)
            {
                return null;
            }
            return clipAudios.TryGetValue(clipId, out AudioStageDetector.Detection ace)
                ? ace
                : null;
        }
        if (suppressNativeFallback)
        {
            return null;
        }
        return nativeFallback;
    }
}
