using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.LTX2;

/// <summary>
/// Owns the state captured from the original post-video chain and exposes
/// stage-facing artifacts derived from that state.
/// </summary>
internal sealed class LtxPostVideoChainCapture
{
    private readonly LtxStageInputArtifactFactory artifacts;
    private readonly LtxAudioReferenceResolver audioReferences;

    public LtxPostVideoChainState State { get; }

    public WGNodeData CurrentOutputMedia => State.CurrentOutputMedia;
    public JArray AvLatentPath => State.AvLatentPath;
    public JArray DecodeOutputPath => State.DecodeOutputPath;
    public bool HasPostDecodeWrappers => State.HasPostDecodeWrappers;

    private LtxPostVideoChainCapture(
        WorkflowGenerator generator,
        ClipAudioState audioReuse,
        LtxPostVideoChainState state)
    {
        State = state;
        audioReferences = new LtxAudioReferenceResolver(generator, audioReuse, state);
        artifacts = new LtxStageInputArtifactFactory(generator, state, audioReferences);
    }

    public static LtxPostVideoChainCapture TryCapture(WorkflowGenerator generator) =>
        TryCaptureCore(generator, audioReuse: null, stage: null, mutateReuseAudioState: false);

    public static LtxPostVideoChainCapture TryCapture(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StagePlan stage) =>
        TryCaptureCore(generator, clipContext.AudioReuse, stage, mutateReuseAudioState: true);

    private static LtxPostVideoChainCapture TryCaptureCore(
        WorkflowGenerator generator,
        ClipAudioState audioReuse,
        StagePlan stage,
        bool mutateReuseAudioState)
    {
        bool useReusedAudio = stage?.AudioAction == StageAudioAction.ReuseCaptured;
        bool captureReusableAudio = stage?.AudioAction == StageAudioAction.CaptureForReuse;
        if (mutateReuseAudioState && !useReusedAudio && !captureReusableAudio)
        {
            audioReuse.Clear();
        }

        LtxPostVideoChainState state =
            LtxPostVideoChainInspector.TryCapture(generator, useReusedAudio);
        if (state is null)
        {
            return null;
        }

        if (mutateReuseAudioState && captureReusableAudio)
        {
            audioReuse.Remember(PathUtils.Clone(state.AudioLatentPath));
        }

        return new LtxPostVideoChainCapture(generator, audioReuse, state);
    }

    public WGNodeData CreateStageInput() => artifacts.CreateStageInput();

    public WGNodeData CreateStageInputVideoLatent() => artifacts.CreateStageInputVideoLatent();

    public WGNodeData CreateStageInputVae() => artifacts.CreateStageInputVae();

    public bool CanReuseCurrentOutputAsStageInput(WGNodeData sourceMedia) =>
        artifacts.CanReuseCurrentOutputAsStageInput(sourceMedia);

    public WGNodeData CreateDetachedGuideMedia(WGNodeData vae) =>
        artifacts.CreateDetachedGuideMedia(vae);

    public void AttachSourceAudio(WGNodeData media) => audioReferences.AttachSourceAudio(media);
}
