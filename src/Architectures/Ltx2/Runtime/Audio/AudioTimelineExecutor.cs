using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Execution.Audio;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class AudioTimelineExecutor
{
    private const long SilenceSampleRate = 44100;
    private const long SilenceChannels = 2;

    private readonly WorkflowGenerator _generator;
    private readonly LtxAudioInjector _audioInjector;

    public AudioTimelineExecutor(
        WorkflowGenerator generator,
        LtxAudioInjector audioInjector)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(audioInjector);
        _generator = generator;
        _audioInjector = audioInjector;
    }

    public void PrepareRootAudio(
        VideoExecutionPlan plan,
        AudioRuntimeSources sources,
        RootPlan root)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(root);
        if (plan.Clips.Count == 0)
        {
            _ = _audioInjector.TryInject(sources.NativeAudio);
            return;
        }
        if (root.UsesStageHandoff
            || plan.Clips[0].EntryMode == ArchitectureEntryMode.InitVideo)
        {
            return;
        }

        ClipPlan first = plan.Clips[0];
        ApplyControlNetClipLength(first);
        if (!first.Audio.Segments.Items.IsDefaultOrEmpty)
        {
            return;
        }
        WGNodeData audio = PlannedAudioSourceSelector.Select(
            first.ClipId,
            first.Audio.Base,
            sources,
            suppressNative: false);
        _ = _audioInjector.TryInject(
            audio,
            first.RequireLtx2Payload().AudioInjection.NonHandoffMatchesAudioLength);
    }

    public void PrepareClipAudio(
        ArchitectureClipRuntimeContext runtimeContext,
        ClipContext clipContext,
        WGNodeData initVideoMedia,
        AudioRuntimeSources rootSources,
        LtxBoundaryAudioCarry boundaryAudioCarry = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);
        ArgumentNullException.ThrowIfNull(clipContext);
        ArgumentNullException.ThrowIfNull(rootSources);
        ClipPlan clip = runtimeContext.Clip;
        ApplyControlNetClipLength(clip);
        AudioRuntimeSources sources =
            initVideoMedia?.AttachedAudio is WGNodeData initVideoAudio
                ? rootSources with { NativeAudio = initVideoAudio }
                : rootSources;
        if (_generator.CurrentMedia is null)
        {
            return;
        }

        WGNodeData currentMedia = _generator.CurrentMedia.Duplicate();
        StagePlan firstStage = clip.Stages.FirstOrDefault();
        RootPlan root = clipContext.Plan.Root;
        int framesPerSecond = clipContext.Plan.FramesPerSecond;
        bool suppressNative = root.UsesStageHandoff
            && root.ReplacesTextToVideoRootStage(firstStage, clip);
        WGNodeData selectedBaseAudio = PlannedAudioSourceSelector.Select(
            clip.ClipId,
            clip.Audio.Base,
            sources,
            suppressNative);
        // Native carry supplies the opening; LTX generates the rest.
        bool carryStartsGeneratedAudio = boundaryAudioCarry is not null
            && clip.Audio.Base.Kind == AudioSourceKind.Native;
        WGNodeData baseAudio = carryStartsGeneratedAudio
            ? null
            : selectedBaseAudio;
        double authoredDuration = ClipAudioBedDuration.Seconds(
            clip,
            framesPerSecond,
            _generator.CurrentMedia);
        double handleDuration = framesPerSecond > 0
            ? clipContext.IncomingContinueHandleFrames / (double)framesPerSecond
            : 0;
        double generationDuration = authoredDuration + handleDuration;
        baseAudio = DelayAuthoredAudio(
            baseAudio,
            handleDuration,
            authoredDuration);
        AudioSegmentPlan segments = handleDuration > 0
            ? new([.. clip.Audio.Segments.Items.Select(segment =>
                segment with { StartSeconds = segment.StartSeconds + handleDuration })])
            : clip.Audio.Segments;

        if (baseAudio is null
            && segments.Items.IsDefaultOrEmpty
            && boundaryAudioCarry?.NativeLatent?.Path is JArray carryPath
            && _generator.CurrentAudioVae is not null)
        {
            int targetFrames = (clip.Frames ?? 0)
                + clipContext.IncomingContinueHandleFrames;
            if (targetFrames > 0 && framesPerSecond > 0)
            {
                using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
                LTXVEmptyLatentAudioNode target = bridge.AddNode(
                    new LTXVEmptyLatentAudioNode().With(
                        FramesNumber: targetFrames,
                        FrameRate: $"{framesPerSecond}",
                        BatchSize: 1));
                target.AudioVae.ConnectFromPath(bridge, _generator.CurrentAudioVae.Path);
                SwarmSetAudioMaskWindowsNode mask = AudioPreserveWindowBuilder.AddMask(
                    _generator,
                    bridge,
                    carryPath,
                    [(0, boundaryAudioCarry.DurationSeconds)],
                    clip.ClipId + 1,
                    target.Latent.ToPath(),
                    boundaryAudioCarry.SourceStartSeconds);
                currentMedia.AttachedAudio = new WGNodeData(
                    mask.Latent.ToPath(),
                    _generator,
                    WGNodeData.DT_LATENT_AUDIO,
                    _generator.CurrentAudioVae.Compat);
                _generator.CurrentMedia = currentMedia;
                return;
            }
        }

        AudioSegmentCombiner combiner = new(_generator);
        WGNodeData baseWithBoundaryCarry = boundaryAudioCarry is null
            ? baseAudio
            : combiner.OverlayOpeningWindow(
                baseAudio,
                boundaryAudioCarry.DecodedSource,
                boundaryAudioCarry.SourceStartSeconds,
                boundaryAudioCarry.DurationSeconds,
                generationDuration);
        WGNodeData combinedAudio = combiner.Combine(
            clip.ClipId,
            segments,
            baseWithBoundaryCarry,
            generationDuration,
            out IReadOnlyList<(double Start, double End)> segmentWindows);
        IReadOnlyList<(double Start, double End)> preserveWindows =
            boundaryAudioCarry is null
                ? segmentWindows
                : [(0, boundaryAudioCarry.DurationSeconds), .. segmentWindows];

        AttachClipAudio(
            runtimeContext,
            clipContext,
            currentMedia,
            baseAudio,
            combinedAudio,
            generationDuration,
            preserveWindows);
    }

    private WGNodeData DelayAuthoredAudio(
        WGNodeData audio,
        double handleDuration,
        double authoredDuration)
    {
        if (audio?.Path is not JArray audioPath || handleDuration <= 0)
        {
            return audio;
        }

        using WorkflowBridge bridge = BridgeSync.For(_generator);
        INodeOutput source = bridge.ResolvePath(audioPath);
        if (source is null)
        {
            return audio;
        }

        SwarmEnsureAudioNode ensure = bridge.AddNode(new SwarmEnsureAudioNode().With(
            TargetDuration: authoredDuration));
        ensure.Audio.ConnectToUntyped(source);
        TrimAudioDurationNode authored = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: 0.0,
            Duration: authoredDuration));
        authored.Audio.ConnectTo(ensure.AUDIO);
        EmptyAudioNode silence = bridge.AddNode(new EmptyAudioNode()).With(
            Duration: handleDuration,
            SampleRate: SilenceSampleRate,
            Channels: SilenceChannels);
        AudioConcatNode concat = bridge.AddNode(
            new AudioConcatNode().With(Direction: "after"));
        concat.Audio1.ConnectTo(silence.AUDIO);
        concat.Audio2.ConnectTo(authored.AUDIO);
        return new WGNodeData(
            WorkflowBridge.ToPath(concat.AUDIO),
            _generator,
            WGNodeData.DT_AUDIO,
            audio.Compat ?? _generator.CurrentAudioVae?.Compat);
    }

    private void AttachClipAudio(
        ArchitectureClipRuntimeContext runtimeContext,
        ClipContext clipContext,
        WGNodeData currentMedia,
        WGNodeData baseAudio,
        WGNodeData combinedAudio,
        double duration,
        IReadOnlyList<(double Start, double End)> preserveWindows)
    {
        ClipPlan clip = runtimeContext.Clip;
        RootPlan root = clipContext.Plan.Root;
        bool hasGenerationStage = clip.Stages.Count > 0;
        bool overlaysOverNoBase = preserveWindows.Count > 0
            && baseAudio is null
            && duration > 0;
        // Do not inject conditioning into a host root this clip replaces.
        bool replacesHostChain = root.DiscardsTextToVideoRoot;
        bool overlaysConditionRootGeneration = hasGenerationStage
            && runtimeContext.ClipIndex == 0
            && overlaysOverNoBase
            && !replacesHostChain
            && _audioInjector.TryInject(
                combinedAudio,
                matchVideoLengthToAudio: false,
                preserveWindows);

        if (overlaysConditionRootGeneration)
        {
            currentMedia.AttachedAudio = null;
        }
        else if (overlaysOverNoBase
            && _audioInjector.TryBuildPreserveWindowedAudioLatent(
                combinedAudio,
                preserveWindows,
                stableIdSlot: clip.ClipId + 1) is WGNodeData windowedLatent)
        {
            currentMedia.AttachedAudio = windowedLatent;
        }
        else
        {
            currentMedia.AttachedAudio = combinedAudio;
            if (hasGenerationStage && overlaysOverNoBase)
            {
                clipContext.PendingAudioConditioning.Defer(combinedAudio, preserveWindows);
            }
        }
        _generator.CurrentMedia = currentMedia;

        if (!hasGenerationStage || replacesHostChain)
        {
            return;
        }

        bool rootInjection = root.UsesStageHandoff
            && clip.RequireLtx2Payload().AudioInjection.RootHandoffMatchesAudioLength;
        if (rootInjection && baseAudio is not null)
        {
            _ = _audioInjector.TryInject(combinedAudio);
        }
        else if (!root.UsesStageHandoff
            && runtimeContext.ClipIndex == 0
            && preserveWindows.Count > 0
            && baseAudio is not null)
        {
            _ = _audioInjector.TryInject(
                combinedAudio,
                clip.RequireLtx2Payload().AudioInjection.NonHandoffMatchesAudioLength);
        }
    }

    public void ApplyControlNetClipLength(ClipPlan plannedClip)
    {
        ArgumentNullException.ThrowIfNull(plannedClip);
        if (plannedClip.Audio.LengthOwner != AudioLengthOwner.ControlNet
            || plannedClip.Stages.Count == 0)
        {
            return;
        }

        int? sourceIndex = (plannedClip.ArchitecturePayload as IArchitectureControlNetSourcePlan)
            ?.ControlNetSourceIndex;
        if (!sourceIndex.HasValue)
        {
            RequestWarnings.Track(
                _generator.UserInput,
                "VideoStages: ControlNet owns clip length, but the compiled plan has no valid "
                + "ControlNet 1-3 source; using the authored clip length instead.");
            return;
        }
        if (!TryApplyControlNetFrameCount(sourceIndex.Value))
        {
            RequestWarnings.Track(
                _generator.UserInput,
                $"VideoStages: ControlNet {sourceIndex.Value + 1} owns clip {plannedClip.ClipId} "
                + "length, but its captured video frame count is unavailable; using the authored "
                + "clip length instead.");
        }
    }

    private bool TryApplyControlNetFrameCount(int controlNetSourceIndex)
    {
        if (!new LtxControlNetMediaNormalizer(_generator).TryCreateFrameCount(
                controlNetSourceIndex,
                out JArray framesConnection))
        {
            return false;
        }
        LtxFrameCountConnector.ApplyToExistingSources(_generator, framesConnection);
        return true;
    }
}
