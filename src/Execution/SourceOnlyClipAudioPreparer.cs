using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Execution;

/// <summary>
/// Builds decoded mux audio for sourced footage. Generation conditioning and model audio latents are
/// deliberately outside this path.
/// </summary>
internal sealed class SourceOnlyClipAudioPreparer(WorkflowGenerator generator)
{
    internal void Prepare(
        SourceOnlyClipExecutionContext context,
        WGNodeData sourcedMedia)
    {
        AudioRuntimeSources sources = sourcedMedia.AttachedAudio is WGNodeData nativeAudio
            ? context.AudioSources with { NativeAudio = nativeAudio }
            : context.AudioSources;
        WGNodeData baseAudio = PlannedAudioSourceSelector.Select(
            context.Clip.ClipId,
            context.Clip.Audio.Base,
            sources,
            suppressNative: false);
        double duration = context.Clip.Frames is int frames
            && sourcedMedia.GetRawFPS() is int fps
            && fps > 0
                ? (double)frames / fps
                : 0;
        WGNodeData combinedAudio = new AudioSegmentCombiner(generator).Combine(
            context.Clip.ClipId,
            context.Clip.Audio.Segments,
            baseAudio,
            duration,
            out _);
        WGNodeData currentMedia = sourcedMedia.Duplicate();
        currentMedia.AttachedAudio = combinedAudio;
        generator.CurrentMedia = currentMedia;
    }
}
