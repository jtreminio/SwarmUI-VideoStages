using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Builds one clip's mux audio and, when it has stages, generation conditioning.</summary>
internal sealed class ClipAudioPreparer(
    WorkflowGenerator g,
    LtxAudioInjector audioInjector)
{
    private const string MergeMethodAdd = "add";
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
        double duration = ClipAudioBedDuration.Seconds(
            context.PlannedClip,
            context.FramesPerSecond,
            g.CurrentMedia);
        WGNodeData baseWithBoundaryCarry = ApplyBoundaryAudioCarry(
            baseAudio,
            boundaryAudioCarry,
            duration);
        WGNodeData combinedAudio = new AudioSegmentCombiner(g).Combine(
            context.PlannedClip.ClipId,
            context.PlannedClip.Audio.Segments,
            baseWithBoundaryCarry,
            duration,
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
            duration,
            preserveWindows);
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
        bool overlaysConditionRootGeneration = hasGenerationStage
            && context.IsFirstClip
            && overlaysOverNoBase
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

        if (!hasGenerationStage)
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

    private WGNodeData ApplyBoundaryAudioCarry(
        WGNodeData baseAudio,
        LtxBoundaryAudioCarry carry,
        double clipDurationSeconds)
    {
        if (carry?.Tail?.Path is not JArray carryPath
            || clipDurationSeconds <= 0)
        {
            return baseAudio;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput carryTail = bridge.ResolvePath(carryPath);
        if (carryTail is null)
        {
            return baseAudio;
        }

        INodeOutput bed = bridge.ResolvePath(baseAudio?.Path);
        if (bed is null)
        {
            EmptyAudioNode silence = bridge.AddNode(new EmptyAudioNode()).With(
                Duration: clipDurationSeconds,
                SampleRate: SilenceSampleRate,
                Channels: SilenceChannels);
            bridge.SyncNode(silence);
            bed = silence.AUDIO;
        }

        AudioMergeNode merge = bridge.AddNode(
            new AudioMergeNode().With(MergeMethod: MergeMethodAdd));
        merge.Audio1.ConnectToUntyped(bed);
        merge.Audio2.ConnectToUntyped(carryTail);
        bridge.SyncNode(merge);
        return new WGNodeData(
            WorkflowBridge.ToPath(merge.AUDIO),
            g,
            WGNodeData.DT_AUDIO,
            baseAudio?.Compat ?? g.CurrentAudioVae?.Compat ?? carry.Tail.Compat);
    }
}
