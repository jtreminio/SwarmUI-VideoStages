using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;

namespace VideoStages;

/// <summary>Builds one clip's mux audio and, when it has stages, generation conditioning.</summary>
internal sealed class ClipAudioPreparer(
    WorkflowGenerator g,
    LtxManager ltxManager)
{
    public void Prepare(ClipAudioExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (g.CurrentMedia is null)
        {
            return;
        }

        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        bool suppressNative = context.RootPolicy.SuppressesNativeAudioForStage(
            context.FirstStage,
            context.PlannedClip);
        WGNodeData baseAudio = PlannedAudioSourceSelector.Select(
            context.PlannedClip.ClipId,
            context.PlannedClip.Audio.Base,
            context.Sources,
            suppressNative);
        int? fps = g.CurrentMedia.GetRawFPS();
        double duration = context.PlannedClip.Frames is int frames && fps is > 0
            ? (double)frames / fps.Value
            : 0;
        WGNodeData combinedAudio = new AudioSegmentCombiner(g).Combine(
            context.PlannedClip.ClipId,
            context.PlannedClip.Audio.Segments,
            baseAudio,
            duration,
            out IReadOnlyList<(double Start, double End)> segmentWindows);

        AttachClipAudio(
            context,
            currentMedia,
            baseAudio,
            combinedAudio,
            duration,
            segmentWindows);
    }

    private void AttachClipAudio(
        ClipAudioExecutionContext context,
        WGNodeData currentMedia,
        WGNodeData baseAudio,
        WGNodeData combinedAudio,
        double duration,
        IReadOnlyList<(double Start, double End)> segmentWindows)
    {
        bool hasGenerationStage = context.FirstStage is not null;
        bool segmentsOverNoBase = segmentWindows.Count > 0
            && baseAudio is null
            && duration > 0;
        bool segmentsConditionGeneration = hasGenerationStage
            && context.IsFirstClip
            && segmentsOverNoBase
            && ltxManager.TryInjectAudio(
                combinedAudio,
                matchVideoLengthToAudio: false,
                preserveWindows: segmentWindows);

        if (segmentsConditionGeneration)
        {
            currentMedia.AttachedAudio = null;
        }
        else if (segmentsOverNoBase
            && ltxManager.TryBuildPreserveWindowedAudioLatent(
                combinedAudio,
                segmentWindows,
                stableIdSlot: context.PlannedClip.ClipId + 1) is WGNodeData windowedLatent)
        {
            currentMedia.AttachedAudio = windowedLatent;
        }
        else
        {
            currentMedia.AttachedAudio = combinedAudio;
        }
        g.CurrentMedia = currentMedia;

        if (!hasGenerationStage)
        {
            return;
        }

        bool rootInjection = context.RootPolicy.UsesStageHandoff
            && context.PlannedClip.Audio.Length.RootHandoffInjectionMatchesAudioLength;
        if (rootInjection && baseAudio is not null)
        {
            _ = ltxManager.TryInjectAudio(combinedAudio);
        }
        else if (!context.RootPolicy.UsesStageHandoff
            && context.IsFirstClip
            && segmentWindows.Count > 0
            && baseAudio is not null)
        {
            _ = ltxManager.TryInjectAudio(
                combinedAudio,
                context.PlannedClip.Audio.Length.NonHandoffInjectionMatchesAudioLength);
        }
    }
}
