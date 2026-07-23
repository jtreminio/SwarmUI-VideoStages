using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Selects one already-resolved runtime source from a compiled base-audio policy.</summary>
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
        return plan.Kind switch
        {
            AudioBaseSourceKind.None => null,
            AudioBaseSourceKind.Native => suppressNative ? null : sources.NativeAudio,
            AudioBaseSourceKind.Upload => ForClip(sources.UploadedAudios, clipId),
            AudioBaseSourceKind.AceStepFun or AudioBaseSourceKind.ControlNet =>
                ForClip(sources.ClipAudios, clipId),
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
