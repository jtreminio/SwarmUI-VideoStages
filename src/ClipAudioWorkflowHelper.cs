using SwarmUI.Builtin_ComfyUIBackend;

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

    internal static WGNodeData ResolveClipAudio(
        int clipId,
        string audioSourceRaw,
        WGNodeData nativeFallback,
        IReadOnlyDictionary<int, WGNodeData> clipAudios,
        IReadOnlyDictionary<int, WGNodeData> uploadedAudios,
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
            ? AudioForClip(uploadedAudios, clipId)
            : AudioForClip(clipAudios, clipId);
    }

    private static WGNodeData AudioForClip(IReadOnlyDictionary<int, WGNodeData> audiosByClipId, int clipId)
    {
        if (audiosByClipId is null)
        {
            return null;
        }
        return audiosByClipId.TryGetValue(clipId, out WGNodeData found) ? found : null;
    }

    internal static bool IsUploadOrAceStepFunAudioSource(string audioSource)
    {
        return string.Equals(audioSource, Constants.AudioSourceUpload, StringComparison.OrdinalIgnoreCase)
            || AudioHandler.TryParseAceStepFunAudioSource(audioSource, out _);
    }
}
