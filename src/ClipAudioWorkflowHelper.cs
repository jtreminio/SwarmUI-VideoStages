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
            return string.Equals(audioSource, Constants.AudioSourceUpload, StringComparison.OrdinalIgnoreCase)
                || AudioStageDetector.TryParseAceStepFunAudioSource(audioSource, out _);
        }

        if (string.Equals(audioSource, Constants.AudioSourceUpload, StringComparison.OrdinalIgnoreCase)
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

        if (string.Equals(source, Constants.AudioSourceUpload, StringComparison.OrdinalIgnoreCase))
        {
            return DetectionForClip(uploadedAudios, clipId);
        }
        if (AudioStageDetector.TryParseAceStepFunAudioSource(source, out _))
        {
            return DetectionForClip(clipAudios, clipId);
        }
        if (suppressNativeFallback)
        {
            return null;
        }
        return nativeFallback;
    }

    private static AudioStageDetector.Detection DetectionForClip(
        IReadOnlyDictionary<int, AudioStageDetector.Detection> audiosByClipId,
        int clipId)
    {
        if (audiosByClipId is null)
        {
            return null;
        }
        return audiosByClipId.TryGetValue(clipId, out AudioStageDetector.Detection found)
            ? found
            : null;
    }
}
