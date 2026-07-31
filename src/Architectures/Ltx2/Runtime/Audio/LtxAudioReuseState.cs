using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal static class LtxAudioReuseState
{
    private static bool IsValidAudioLatentPath(JArray path)
    {
        return path is { Count: 2 };
    }

    public static void PrepareReusableAudio(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StagePlan stage)
    {
        if (generator.CurrentMedia is null)
        {
            return;
        }

        Ltx2ClipAudioReuseState audioReuse = clipContext.AudioReuse;
        StageAudioAction action = stage.RequireLtx2Payload().AudioAction;
        if (action == StageAudioAction.None)
        {
            audioReuse.Clear();
            return;
        }

        if (action == StageAudioAction.CaptureForReuse
            && generator.CurrentMedia.AttachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO
            && generator.CurrentMedia.AttachedAudio.Path is JArray currentAudioPath
            && IsValidAudioLatentPath(currentAudioPath))
        {
            audioReuse.Remember(new JArray(currentAudioPath[0], currentAudioPath[1]));
            return;
        }

        if (!audioReuse.TryGetPath(out JArray reusedAudioPath))
        {
            return;
        }

        T2IModelCompatClass audioCompat = generator.CurrentAudioVae?.Compat
            ?? generator.CurrentMedia.AttachedAudio?.Compat
            ?? T2IModelClassSorter.CompatLtxv2;
        WGNodeData currentMedia = generator.CurrentMedia.Duplicate();
        currentMedia.AttachedAudio = new WGNodeData(
            new JArray(reusedAudioPath[0], reusedAudioPath[1]),
            generator,
            WGNodeData.DT_LATENT_AUDIO,
            audioCompat);
        generator.CurrentMedia = currentMedia;
    }

    internal static bool UsesCapturedAudio(StagePlan stage) =>
        stage?.RequireLtx2Payload().AudioAction == StageAudioAction.ReuseCaptured;

    /// <summary>
    /// Completes the capture transition when the latent is discoverable from the post-video chain
    /// but was not exposed through <c>CurrentMedia.AttachedAudio</c>. All mutation of the clip-local
    /// reuse state remains owned here.
    /// </summary>
    internal static void CompletePostVideoChainCapture(
        Ltx2ClipAudioReuseState audioReuse,
        StagePlan stage,
        LtxPostVideoChainState captured)
    {
        if (audioReuse is null
            || captured is null
            || stage?.RequireLtx2Payload().AudioAction != StageAudioAction.CaptureForReuse)
        {
            return;
        }
        audioReuse.Remember(PathUtils.Clone(captured.AudioLatentPath));
    }
}

internal sealed class Ltx2ClipAudioReuseState
{
    public JArray ReusedAudioPath { get; private set; }

    public bool TryGetPath(out JArray path)
    {
        path = ReusedAudioPath;
        return path is not null;
    }

    public void Remember(JArray path) => ReusedAudioPath = path;

    public void Clear() => ReusedAudioPath = null;
}
