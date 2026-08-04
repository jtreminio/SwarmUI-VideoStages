using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Builds one clip's mux audio and, when it has stages, generation conditioning.</summary>
internal sealed class ClipAudioPreparer(
    WorkflowGenerator g,
    LtxAudioInjector audioInjector)
{
    private const long SilenceSampleRate = 44100;
    private const long SilenceChannels = 2;

    public void Prepare(
        ClipAudioExecutionContext context,
        ClipContext clipContext,
        LtxBoundaryAudioCarry boundaryAudioCarry = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clipContext);
        if (g.CurrentMedia is null)
        {
            return;
        }

        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        bool suppressNative = context.RootPolicy.SuppressesNativeAudioForStage(
            context.FirstStage,
            context.PlannedClip);
        WGNodeData selectedBaseAudio = PlannedAudioSourceSelector.Select(
            context.PlannedClip.ClipId,
            context.PlannedClip.Audio.Base,
            context.Sources,
            suppressNative);
        // Native audio is the model-generated/default track, not an authored full-clip bed.
        // Boundary carry replaces its opening with preserved context and lets LTX generate the rest.
        bool carryStartsGeneratedAudio = boundaryAudioCarry is not null
            && context.PlannedClip.Audio.Base.Kind == AudioSourceKind.Native;
        WGNodeData baseAudio = carryStartsGeneratedAudio
            ? null
            : selectedBaseAudio;
        double authoredDuration = ClipAudioBedDuration.Seconds(
            context.PlannedClip,
            context.FramesPerSecond,
            g.CurrentMedia);
        double handleDuration = context.FramesPerSecond > 0
            ? clipContext.IncomingContinueHandleFrames / (double)context.FramesPerSecond
            : 0;
        double generationDuration = authoredDuration + handleDuration;
        baseAudio = DelayAuthoredAudio(
            baseAudio,
            handleDuration,
            authoredDuration);
        AudioSegmentPlan segments = handleDuration > 0
            ? new([.. context.PlannedClip.Audio.Segments.Items.Select(segment =>
                segment with { StartSeconds = segment.StartSeconds + handleDuration })])
            : context.PlannedClip.Audio.Segments;

        if (baseAudio is null
            && segments.Items.IsDefaultOrEmpty
            && boundaryAudioCarry?.NativeLatent?.Path is JArray carryPath
            && g.CurrentAudioVae is not null)
        {
            int targetFrames = (context.PlannedClip.Frames ?? 0)
                + clipContext.IncomingContinueHandleFrames;
            if (targetFrames > 0 && context.FramesPerSecond > 0)
            {
                using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
                LTXVEmptyLatentAudioNode target = bridge.AddNode(
                    new LTXVEmptyLatentAudioNode().With(
                        FramesNumber: targetFrames,
                        FrameRate: $"{context.FramesPerSecond}",
                        BatchSize: 1));
                target.AudioVae.ConnectFromPath(bridge, g.CurrentAudioVae.Path);
                SwarmSetAudioMaskWindowsNode mask = AudioPreserveWindowBuilder.AddMask(
                    g,
                    bridge,
                    carryPath,
                    [(0, boundaryAudioCarry.DurationSeconds)],
                    context.PlannedClip.ClipId + 1,
                    target.Latent.ToPath(),
                    boundaryAudioCarry.SourceStartSeconds);
                currentMedia.AttachedAudio = new WGNodeData(
                    mask.Latent.ToPath(),
                    g,
                    WGNodeData.DT_LATENT_AUDIO,
                    g.CurrentAudioVae.Compat);
                g.CurrentMedia = currentMedia;
                return;
            }
        }

        AudioSegmentCombiner combiner = new(g);
        WGNodeData baseWithBoundaryCarry = boundaryAudioCarry is null
            ? baseAudio
            : combiner.OverlayOpeningWindow(
                baseAudio,
                boundaryAudioCarry.DecodedSource,
                boundaryAudioCarry.SourceStartSeconds,
                boundaryAudioCarry.DurationSeconds,
                generationDuration);
        WGNodeData combinedAudio = combiner.Combine(
            context.PlannedClip.ClipId,
            segments,
            baseWithBoundaryCarry,
            generationDuration,
            out IReadOnlyList<(double Start, double End)> segmentWindows);
        IReadOnlyList<(double Start, double End)> preserveWindows =
            boundaryAudioCarry is null
                ? segmentWindows
                : [(0, boundaryAudioCarry.DurationSeconds), .. segmentWindows];

        AttachClipAudio(
            context,
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

        using WorkflowBridge bridge = BridgeSync.For(g);
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
            g,
            WGNodeData.DT_AUDIO,
            audio.Compat ?? g.CurrentAudioVae?.Compat);
    }

    private void AttachClipAudio(
        ClipAudioExecutionContext context,
        ClipContext clipContext,
        WGNodeData currentMedia,
        WGNodeData baseAudio,
        WGNodeData combinedAudio,
        double duration,
        IReadOnlyList<(double Start, double End)> preserveWindows)
    {
        bool hasGenerationStage = context.FirstStage is not null;
        bool overlaysOverNoBase = preserveWindows.Count > 0
            && baseAudio is null
            && duration > 0;
        // Injecting conditions the host's own root chain, which is only worth doing while that
        // chain is what generates. Under a discarded text root the first clip's stage replaces it,
        // so every injection below is skipped: this one used to leave the clip generating
        // unconditioned audio, because clearing AttachedAudio let the stage build a fresh empty
        // latent over the injected one, and the two later ones merely ship a duplicate encode chain
        // ComfyUI runs for nothing. Evaluated before TryInject: the call is the injection.
        bool replacesHostChain = context.RootPolicy.DiscardsTextToVideoRoot;
        bool overlaysConditionRootGeneration = hasGenerationStage
            && context.IsFirstClip
            && overlaysOverNoBase
            && !replacesHostChain
            && audioInjector.TryInject(
                combinedAudio,
                matchVideoLengthToAudio: false,
                preserveWindows);

        if (overlaysConditionRootGeneration)
        {
            currentMedia.AttachedAudio = null;
        }
        else if (overlaysOverNoBase
            && audioInjector.TryBuildPreserveWindowedAudioLatent(
                combinedAudio,
                preserveWindows,
                stableIdSlot: context.PlannedClip.ClipId + 1) is WGNodeData windowedLatent)
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
        g.CurrentMedia = currentMedia;

        if (!hasGenerationStage || replacesHostChain)
        {
            return;
        }

        bool rootInjection = context.RootPolicy.UsesStageHandoff
            && context.PlannedClip.RequireLtx2Payload().AudioInjection.RootHandoffMatchesAudioLength;
        if (rootInjection && baseAudio is not null)
        {
            _ = audioInjector.TryInject(combinedAudio);
        }
        else if (!context.RootPolicy.UsesStageHandoff
            && context.IsFirstClip
            && preserveWindows.Count > 0
            && baseAudio is not null)
        {
            _ = audioInjector.TryInject(
                combinedAudio,
                context.PlannedClip.RequireLtx2Payload().AudioInjection.NonHandoffMatchesAudioLength);
        }
    }

}
