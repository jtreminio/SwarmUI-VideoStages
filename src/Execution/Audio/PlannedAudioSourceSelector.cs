using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Execution.Audio;

/// <summary>Resolves a compiled selection, using native audio when its external source is absent.</summary>
internal static class PlannedAudioSourceSelector
{
    public static WGNodeData Select(
        int clipId,
        AudioBaseSourcePlan plan,
        AudioRuntimeSources sources,
        bool suppressNative)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        WGNodeData nativeAudio = suppressNative ? null : sources.NativeAudio;
        return plan.Kind switch
        {
            AudioSourceKind.Native => nativeAudio,
            AudioSourceKind.Upload =>
                ForClip(sources.UploadedAudios, clipId) ?? nativeAudio,
            AudioSourceKind.AceStepFun or AudioSourceKind.ControlNet =>
                ForClip(sources.ClipAudios, clipId) ?? nativeAudio,
            _ => null,
        };
    }

    private static WGNodeData ForClip(
        IReadOnlyDictionary<int, WGNodeData> sources,
        int clipId) =>
        sources is not null && sources.TryGetValue(clipId, out WGNodeData audio)
            ? audio
            : null;
}
