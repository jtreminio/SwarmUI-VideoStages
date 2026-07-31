using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Execution;

/// <summary>
/// Builds decoded mux audio for initVideoClip footage. Generation conditioning and model audio latents are
/// deliberately outside this path.
/// </summary>
internal sealed class SourceOnlyClipAudioPreparer(WorkflowGenerator generator)
{
    internal void Prepare(
        ClipPlan clip,
        int framesPerSecond,
        AudioRuntimeSources audioSources,
        WGNodeData initVideoMedia)
    {
        AudioRuntimeSources sources = initVideoMedia.AttachedAudio is WGNodeData nativeAudio
            ? audioSources with { NativeAudio = nativeAudio }
            : audioSources;
        WGNodeData baseAudio = PlannedAudioSourceSelector.Select(
            clip.ClipId,
            clip.Audio.Base,
            sources,
            suppressNative: false);
        double duration = ClipAudioBedDuration.Seconds(
            clip,
            framesPerSecond,
            initVideoMedia);
        WGNodeData combinedAudio = new AudioSegmentCombiner(generator).Combine(
            clip.ClipId,
            clip.Audio.Segments,
            baseAudio,
            duration,
            out _);
        WGNodeData currentMedia = initVideoMedia.Duplicate();
        currentMedia.AttachedAudio = combinedAudio;
        generator.CurrentMedia = currentMedia;
    }
}
