namespace VideoStages;

internal static class ClipAudioWorkflowHelper
{
    internal static bool IsExternalClipAudioSource(string audioSource)
    {
        string source = audioSource?.Trim();
        return StringUtils.Equals(source, Constants.AudioSourceUpload)
            || StringUtils.Equals(source, Constants.AudioSourceControlNet)
            || AudioHandler.TryParseAceStepFunAudioSource(source, out _);
    }
}
