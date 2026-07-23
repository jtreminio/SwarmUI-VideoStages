using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.LTX2;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Runtime sources prepared once by <see cref="AudioTimelineExecutor"/> and shared by root and
/// per-clip audio decisions.
/// </summary>
internal sealed record ClipAudioRuntimeSources(
    WGNodeData NativeAudio,
    IReadOnlyDictionary<int, WGNodeData> ClipAudios,
    IReadOnlyDictionary<int, WGNodeData> UploadedAudios,
    bool RootStageHandoff);

internal sealed record PreparedAudioRuntimeSources(
    WGNodeData NativeAudio,
    IReadOnlyDictionary<int, WGNodeData> ClipAudios,
    IReadOnlyDictionary<int, WGNodeData> UploadedAudios);

/// <summary>
/// One planned clip entering audio preparation. Runtime media is resolved against the immutable
/// audio policy carried by <see cref="PlannedClip"/>.
/// </summary>
internal sealed record ClipAudioExecutionContext(
    ClipSpec Clip,
    StagePlan FirstStage,
    ClipPlan PlannedClip,
    bool IsFirstClip,
    ClipAudioRuntimeSources Sources);

/// <summary>
/// The runtime owner of planned per-clip audio preparation. Pending or provisional multi-clip
/// spans remain atomic plan data until runtime boundary validation can reconcile their windows.
/// </summary>
internal sealed class AudioTimelineExecutor(
    WorkflowGenerator g,
    RootVideoStageHandoff rootVideoStageHandoff,
    LtxManager ltxManager,
    AudioHandler audioHandler)
{
    /// <summary>Resolves configured audio sources once and prunes unsaved ACE tracks.</summary>
    public PreparedAudioRuntimeSources PrepareRuntimeSources(
        IReadOnlyList<ClipSpec> clips,
        VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(plan);
        IReadOnlyDictionary<int, ClipPlan> plannedClips =
            plan.Clips.ToDictionary(clip => clip.ClipId);
        Dictionary<int, WGNodeData> clipAudios = [];
        foreach (ClipSpec clip in clips)
        {
            if (!plannedClips.TryGetValue(clip.Id, out ClipPlan plannedClip))
            {
                continue;
            }
            WGNodeData ace = plannedClip.Audio.Base.Kind == AudioBaseSourceKind.AceStepFun
                ? audioHandler.DetectAceStepFunAudio(plannedClip.Audio.Base.RawSource)
                : null;
            if (ace is not null)
            {
                clipAudios[clip.Id] = ace;
            }
        }
        ControlNetCapture capture = new(g);
        foreach (ClipSpec clip in clips)
        {
            if (plannedClips.TryGetValue(clip.Id, out ClipPlan plannedClip)
                && plannedClip.Audio.Base.Kind == AudioBaseSourceKind.ControlNet
                && capture.TryGetCapturedControlNetAudio(clip.PrimarySlotEntry?.Source, out WGNodeData audio))
            {
                clipAudios[clip.Id] = audio;
            }
        }

        Dictionary<int, WGNodeData> uploadedAudios = [];
        foreach (ClipSpec clip in clips)
        {
            if (!plannedClips.TryGetValue(clip.Id, out ClipPlan plannedClip)
                || plannedClip.Audio.Base.Kind != AudioBaseSourceKind.Upload)
            {
                continue;
            }
            AudioFile uploaded = VideoStagesSpecParser.MaterializeUploadedAudioForClip(g, clip);
            if (uploaded is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(uploaded, "${vsaudioupload}");
            uploadedAudios[clip.Id] = new WGNodeData(
                new JArray(loadNodeId, 0),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
        }
        audioHandler.PruneAceStepFunUnsavedTracks(clips);
        return new(g.CurrentMedia?.AttachedAudio, clipAudios, uploadedAudios);
    }

    /// <summary>Applies the root-level first-clip injection before stage execution.</summary>
    public void PrepareRootAudio(
        IReadOnlyList<ClipSpec> clips,
        VideoExecutionPlan plan,
        PreparedAudioRuntimeSources sources,
        bool rootStageHandoff,
        bool firstClipSourced)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        if (clips.Count == 0)
        {
            _ = ltxManager.TryInjectAudio(g.CurrentMedia?.AttachedAudio);
            return;
        }
        if (rootStageHandoff || firstClipSourced || plan.Clips.Count == 0)
        {
            return;
        }
        ClipSpec firstClip = clips[0];
        ClipPlan firstPlan = plan.Clips[0];
        if (firstPlan.Stages.Count > 0)
        {
            ApplyControlNetClipLength(firstClip, firstPlan);
        }
        if (!firstPlan.Audio.Segments.Items.IsDefaultOrEmpty)
        {
            return;
        }
        WGNodeData audio = ResolvePlannedBaseAudio(
            firstClip.Id,
            firstPlan.Audio.Base,
            sources.NativeAudio,
            sources.ClipAudios,
            sources.UploadedAudios,
            suppressNativeFallback: false);
        _ = ltxManager.TryInjectAudio(
            audio,
            firstPlan.Audio.Length.NonHandoffInjectionMatchesAudioLength);
    }
    public void PrepareClipAudio(ClipAudioExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (g.CurrentMedia is null)
        {
            return;
        }

        ClipSpec clip = context.Clip;
        ClipAudioRuntimeSources sources = context.Sources;
        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        bool suppressNative = sources.RootStageHandoff
            && rootVideoStageHandoff.ShouldReplaceTextToVideoRootStage(
                context.FirstStage,
                context.PlannedClip);
        WGNodeData clipAudio = ResolvePlannedBaseAudio(
            clip.Id,
            context.PlannedClip.Audio.Base,
            sources.NativeAudio,
            sources.ClipAudios,
            sources.UploadedAudios,
            suppressNative);

        // Overlay per-clip segments before the parallel merge, so its boundary trims are shared by
        // the resulting audio. Timeline-owned tracks are intentionally not mixed here.
        int? clipFps = g.CurrentMedia.GetRawFPS();
        double clipDurationSeconds = clip.Frames is int frames && clipFps is int fps && fps > 0
            ? (double)frames / fps
            : 0;
        WGNodeData combinedAudio = new AudioSegmentCombiner(g).Combine(
            clip,
            clipAudio,
            clipDurationSeconds,
            out IReadOnlyList<(double Start, double End)> segmentWindows);

        InjectClipConditioningAudio(
            clip,
            context.IsFirstClip,
            sources.RootStageHandoff,
            context.PlannedClip.Audio,
            currentMedia,
            clipAudio,
            combinedAudio,
            clipDurationSeconds,
            segmentWindows);
    }

    /// <summary>
    /// Conditions via an injected latent when possible,
    /// otherwise attach either a preserve-windowed latent or decoded audio to current media.
    /// </summary>
    private void InjectClipConditioningAudio(
        ClipSpec clip,
        bool isFirstClip,
        bool rootStageHandoff,
        AudioPlan audioPlan,
        WGNodeData currentMedia,
        WGNodeData clipAudio,
        WGNodeData combinedAudio,
        double clipDurationSeconds,
        IReadOnlyList<(double Start, double End)> segmentWindows)
    {
        bool segmentsOverNoBase = segmentWindows.Count > 0
            && clipAudio is null
            && clipDurationSeconds > 0;
        bool segmentsConditionGeneration = isFirstClip
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
                combinedAudio, segmentWindows, stableIdSlot: clip.Id + 1) is WGNodeData windowedLatent)
        {
            currentMedia.AttachedAudio = windowedLatent;
        }
        else
        {
            currentMedia.AttachedAudio = combinedAudio;
        }
        g.CurrentMedia = currentMedia;

        bool uploadInjectPath =
            rootStageHandoff && audioPlan.Length.RootHandoffInjectionMatchesAudioLength;
        if (uploadInjectPath && clipAudio is not null)
        {
            _ = ltxManager.TryInjectAudio(combinedAudio);
        }
        else if (!rootStageHandoff
            && isFirstClip
            && segmentWindows.Count > 0
            && clipAudio is not null)
        {
            _ = ltxManager.TryInjectAudio(
                combinedAudio,
                audioPlan.Length.NonHandoffInjectionMatchesAudioLength);
        }
    }

    public void ApplyControlNetClipLength(ClipSpec clip, ClipPlan plannedClip)
    {
        if (plannedClip.Audio.Length.Owner == AudioLengthOwner.ControlNet
            && plannedClip.Stages.Count > 0)
        {
            _ = ltxManager.TryApplyControlNetFrameCount(clip.PrimarySlotEntry?.Source);
        }
    }

    private static WGNodeData ResolvePlannedBaseAudio(
        int clipId,
        AudioBaseSourcePlan audioPlan,
        WGNodeData nativeAudio,
        IReadOnlyDictionary<int, WGNodeData> clipAudios,
        IReadOnlyDictionary<int, WGNodeData> uploadedAudios,
        bool suppressNativeFallback)
    {
        return audioPlan.Kind switch
        {
            AudioBaseSourceKind.None => null,
            AudioBaseSourceKind.Native => suppressNativeFallback ? null : nativeAudio,
            AudioBaseSourceKind.Upload => AudioForClip(uploadedAudios, clipId),
            AudioBaseSourceKind.AceStepFun or AudioBaseSourceKind.ControlNet =>
                AudioForClip(clipAudios, clipId),
            _ => null,
        };
    }

    private static WGNodeData AudioForClip(
        IReadOnlyDictionary<int, WGNodeData> audios,
        int clipId) =>
        audios is not null && audios.TryGetValue(clipId, out WGNodeData audio)
            ? audio
            : null;
}
