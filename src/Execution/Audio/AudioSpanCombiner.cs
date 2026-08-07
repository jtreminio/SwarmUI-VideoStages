using System.Collections.Immutable;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Planning;

namespace VideoStages.Execution.Audio;

/// <summary>Combines one clip's base and overlay audio before the cross-clip merge.</summary>
internal sealed class AudioSpanCombiner(WorkflowGenerator g)
{
    private const long SilenceSampleRate = 44100;
    private const long SilenceChannels = 2;
    private const string MergeMethodAdd = "add";
    private const string ConcatDirectionAfter = "after";

    /// <summary>
    /// Returns <paramref name="baseAudio"/> unchanged when no span materializes and creates a
    /// silent bed when spans have no base. <paramref name="loadedWindows"/> reports materialized
    /// time windows for generation conditioning.
    /// </summary>
    public WGNodeData Combine(
        int clipId,
        ImmutableArray<AudioSpanPlan> spans,
        WGNodeData baseAudio,
        double clipDurationSeconds,
        out IReadOnlyList<(double Start, double End)> loadedWindows)
    {
        loadedWindows = [];
        if (spans.IsDefaultOrEmpty)
        {
            return baseAudio;
        }

        // Materialize the span sources into node paths before opening the bridge — CreateAudioLoadNode
        // writes to g.Workflow directly, and the AceStepFun lookup reads via its own bridge (one shared
        // parse for all spans).
        AudioHandler audioHandler = new(g);
        WorkflowBridge detectBridge = null;
        List<(AudioSpanPlan Plan, JArray Path)> loaded = [];
        foreach (AudioSpanPlan span in spans)
        {
            if (span.SourceKind == AudioSourceKind.AceStepFun)
            {
                detectBridge ??= WorkflowBridge.Create(g.Workflow);
                WGNodeData ace = span.AceStepFunTrack is int track
                    ? audioHandler.DetectAceStepFunAudio(track, detectBridge)
                    : null;
                if (ace?.Path is JArray acePath)
                {
                    loaded.Add((span, acePath));
                }
                else
                {
                    RequestWarnings.Track(
                        g.UserInput,
                        $"VideoStages: clip {clipId} audio track references AceStepFun track "
                        + $"'{span.AceStepFunTrack}', which is not present in the workflow; skipping the span.");
                }
                continue;
            }
            AudioFile file = UploadedMedia.GetAudio(g.UserInput, span.UploadedMedia);
            if (file is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(file, "${vsaudiospan}");
            loaded.Add((span, new JArray(loadNodeId, 0)));
        }
        detectBridge?.Dispose();
        if (loaded.Count == 0)
        {
            return baseAudio;
        }
        loadedWindows = [.. loaded.Select(entry =>
            (entry.Plan.StartSeconds, entry.Plan.StartSeconds + entry.Plan.LengthSeconds))];

        using WorkflowBridge bridge = BridgeSync.For(g);

        INodeOutput accumulator = bridge.ResolvePath(baseAudio?.Path);
        if (accumulator is null && clipDurationSeconds > 0)
        {
            accumulator = Silence(bridge, clipDurationSeconds);
        }

        foreach ((AudioSpanPlan span, JArray path) in loaded)
        {
            INodeOutput source = bridge.ResolvePath(path);
            if (source is null)
            {
                continue;
            }
            INodeOutput placed = PlaceSpan(bridge, source, span);
            accumulator = accumulator is null ? placed : Merge(bridge, accumulator, placed);
        }

        if (accumulator is null)
        {
            return baseAudio;
        }
        return new WGNodeData(
            WorkflowBridge.ToPath(accumulator),
            g,
            WGNodeData.DT_AUDIO,
            baseAudio?.Compat ?? g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
    }

    internal WGNodeData OverlayOpeningWindow(
        WGNodeData baseAudio,
        WGNodeData overlayAudio,
        double sourceStartSeconds,
        double overlayDurationSeconds,
        double clipDurationSeconds)
    {
        if (overlayAudio?.Path is not JArray overlayPath
            || overlayDurationSeconds <= 0
            || clipDurationSeconds <= 0)
        {
            return baseAudio;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput source = bridge.ResolvePath(overlayPath);
        if (source is null)
        {
            return baseAudio;
        }
        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: sourceStartSeconds,
            Duration: overlayDurationSeconds);
        trim.Audio.ConnectToUntyped(source);

        INodeOutput bed = bridge.ResolvePath(baseAudio?.Path)
            ?? Silence(bridge, clipDurationSeconds);
        return new WGNodeData(
            WorkflowBridge.ToPath(Merge(bridge, bed, trim.AUDIO)),
            g,
            WGNodeData.DT_AUDIO,
            baseAudio?.Compat ?? g.CurrentAudioVae?.Compat ?? overlayAudio.Compat);
    }

    private static INodeOutput PlaceSpan(
        WorkflowBridge bridge,
        INodeOutput source,
        AudioSpanPlan span)
    {
        // Pad the source up to trim-start + length first: TrimAudioDuration returns the shorter clip
        // when the source runs out (so the span's declared window would lock silent bed in the
        // conditioning mask), and it throws outright when the trim start is past the source's end.
        // Padding makes the declared window authoritative: a short source plays, then intended silence.
        SwarmEnsureAudioNode ensure = bridge.AddNode(new SwarmEnsureAudioNode().With(
            TargetDuration: span.TrimStartSeconds + span.LengthSeconds));
        ensure.Audio.ConnectToUntyped(source);

        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: span.TrimStartSeconds,
            Duration: span.LengthSeconds);
        trim.Audio.ConnectTo(ensure.AUDIO);
        INodeOutput trimmed = ApplyVolume(bridge, trim.AUDIO, span.Volume);

        if (span.StartSeconds <= 0)
        {
            return trimmed;
        }

        INodeOutput silence = Silence(bridge, span.StartSeconds);
        AudioConcatNode concat = bridge.AddNode(new AudioConcatNode()).With(Direction: ConcatDirectionAfter);
        concat.Audio1.ConnectToUntyped(silence);
        concat.Audio2.ConnectToUntyped(trimmed);
        return concat.AUDIO;
    }

    /// <summary>
    /// Pushes <paramref name="audio"/> back behind <paramref name="delaySeconds"/> of silence,
    /// padded or trimmed to <paramref name="audioDuration"/> so the delay is the only change.
    /// </summary>
    internal WGNodeData DelayBy(WGNodeData audio, double delaySeconds, double audioDuration)
    {
        if (audio?.Path is not JArray audioPath || delaySeconds <= 0)
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
            TargetDuration: audioDuration));
        ensure.Audio.ConnectToUntyped(source);
        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: 0.0,
            Duration: audioDuration));
        trim.Audio.ConnectTo(ensure.AUDIO);
        AudioConcatNode concat = bridge.AddNode(
            new AudioConcatNode().With(Direction: ConcatDirectionAfter));
        concat.Audio1.ConnectToUntyped(Silence(bridge, delaySeconds));
        concat.Audio2.ConnectTo(trim.AUDIO);
        return new WGNodeData(
            WorkflowBridge.ToPath(concat.AUDIO),
            g,
            WGNodeData.DT_AUDIO,
            audio.Compat ?? g.CurrentAudioVae?.Compat);
    }

    private static INodeOutput ApplyVolume(
        WorkflowBridge bridge,
        INodeOutput audio,
        double volume)
    {
        double normalized = double.IsFinite(volume)
            ? Math.Clamp(volume, 0.00001, 100000.0)
            : 1.0;

        int decibels = (int)Math.Round(
            20 * Math.Log10(normalized),
            MidpointRounding.AwayFromZero);
        if (decibels == 0)
        {
            return audio;
        }
        AudioAdjustVolumeNode adjust = bridge.AddNode(
            new AudioAdjustVolumeNode().With(Volume: decibels));
        adjust.Audio.ConnectToUntyped(audio);
        return adjust.AUDIO;
    }

    /// <summary>The extension's one silent-audio bed; every silence in the graph comes from here.</summary>
    internal static INodeOutput Silence(WorkflowBridge bridge, double durationSeconds)
    {
        EmptyAudioNode empty = bridge.AddNode(new EmptyAudioNode()).With(
            Duration: durationSeconds,
            SampleRate: SilenceSampleRate,
            Channels: SilenceChannels);
        return empty.AUDIO;
    }

    private static INodeOutput Merge(WorkflowBridge bridge, INodeOutput baseAudio, INodeOutput overlay)
    {
        AudioMergeNode merge = bridge.AddNode(new AudioMergeNode()).With(MergeMethod: MergeMethodAdd);
        merge.Audio1.ConnectToUntyped(baseAudio);
        merge.Audio2.ConnectToUntyped(overlay);
        return merge.AUDIO;
    }
}
