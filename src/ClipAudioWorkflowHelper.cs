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
        return IsUploadOrAceStepFunAudioSource(audioSource)
            ? clipLengthFromAudio
            : !restrictLengthMatchToUploadOrAce;
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
                break;
            case ClipAudioSourceNormalization.StageSpec:
                source = (audioSourceRaw ?? Constants.AudioSourceNative).Trim();
                if (source.Length == 0)
                {
                    return null;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(normalization), normalization, null);
        }

        if (!IsUploadOrAceStepFunAudioSource(source))
        {
            return suppressNativeFallback ? null : nativeFallback;
        }
        return string.Equals(source, Constants.AudioSourceUpload, StringComparison.OrdinalIgnoreCase)
            ? DetectionForClip(uploadedAudios, clipId)
            : DetectionForClip(clipAudios, clipId);
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

    internal static bool IsUploadOrAceStepFunAudioSource(string audioSource)
    {
        return string.Equals(audioSource, Constants.AudioSourceUpload, StringComparison.OrdinalIgnoreCase)
            || AudioStageDetector.TryParseAceStepFunAudioSource(audioSource, out _);
    }
}
