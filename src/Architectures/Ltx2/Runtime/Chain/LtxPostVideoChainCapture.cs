using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

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
        Ltx2ClipAudioReuseState audioReuse,
        LtxPostVideoChainState state)
    {
        State = state;
        audioReferences = new LtxAudioReferenceResolver(generator, audioReuse, state);
        artifacts = new LtxStageInputArtifactFactory(generator, state, audioReferences);
    }

    public static LtxPostVideoChainCapture TryCapture(WorkflowGenerator generator) =>
        TryCaptureCore(generator, audioReuse: null, stage: null);

    public static LtxPostVideoChainCapture TryCapture(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StagePlan stage) =>
        TryCaptureCore(generator, clipContext.AudioReuse, stage);

    private static LtxPostVideoChainCapture TryCaptureCore(
        WorkflowGenerator generator,
        Ltx2ClipAudioReuseState audioReuse,
        StagePlan stage)
    {
        bool useReusedAudio = LtxAudioReuseState.UsesCapturedAudio(stage);
        LtxPostVideoChainState state =
            LtxPostVideoChainInspector.TryCapture(generator, useReusedAudio);
        if (state is null)
        {
            return null;
        }

        LtxAudioReuseState.CompletePostVideoChainCapture(
            audioReuse,
            stage,
            state);

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
