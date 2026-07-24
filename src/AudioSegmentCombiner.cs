using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Combines a clip's optional overlay audio segments with its base audio, before the cross-clip merge.
/// Each segment is trimmed (<see cref="TrimAudioDurationNode"/>, seconds), offset by prepending
/// silence (<see cref="EmptyAudioNode"/> + <see cref="AudioConcatNode"/>), attenuated when requested
/// (<see cref="AudioAdjustVolumeNode"/>), then mixed additively over the running base with
/// <see cref="AudioMergeNode"/> (<c>merge_method="add"</c>, which pads/trims the overlay to the base
/// length). A clip with no segments returns its base audio untouched — the pure existing graph is
/// preserved (regression lock). The combined result is used both as the mux-time audio track and, via
/// <see cref="StageSequenceRunner"/>, as generation-time conditioning audio (segments baked into the
/// preserved track, or preserve-windowed over a silent bed when there is no locked base).
/// </summary>
internal sealed class AudioSegmentCombiner(WorkflowGenerator g)
{
    private const long SilenceSampleRate = 44100;
    private const long SilenceChannels = 2;
    private const string SegmentUploadPlaceholder = "${vsaudioseg}";
    private const string MergeMethodAdd = "add";
    private const string ConcatDirectionAfter = "after";

    /// <summary>
    /// Returns the base audio overlaid with the clip's segments, or <paramref name="baseAudio"/> unchanged
    /// when the clip has no (materializable) segments. When the clip has segments but no base audio, a
    /// silent bed of the clip duration is synthesized so the segments still play at their offsets.
    /// <paramref name="loadedWindows"/> reports the seconds-window of every segment that actually
    /// materialized, for generation-time audio conditioning (preserve windows).
    /// </summary>
    public WGNodeData Combine(
        int clipId,
        AudioSegmentPlan segmentPlan,
        WGNodeData baseAudio,
        double clipDurationSeconds,
        out IReadOnlyList<(double Start, double End)> loadedWindows)
    {
        loadedWindows = [];
        if (segmentPlan.Items.IsDefaultOrEmpty)
        {
            return baseAudio;
        }

        // Materialize the segment sources into node paths BEFORE opening the bridge — CreateAudioLoadNode
        // writes to g.Workflow directly, and the AceStepFun lookup reads via its own bridge (one shared
        // parse for all segments).
        AudioHandler audioHandler = new(g);
        WorkflowBridge detectBridge = null;
        List<(AudioSegmentItemPlan Plan, JArray Path)> loaded = [];
        foreach (AudioSegmentItemPlan segment in segmentPlan.Items)
        {
            if (segment.SourceKind == AudioSourceKind.AceStepFun)
            {
                detectBridge ??= WorkflowBridge.Create(g.Workflow);
                WGNodeData ace = segment.AceStepFunTrack is int track
                    ? audioHandler.DetectAceStepFunAudio(track, detectBridge)
                    : null;
                if (ace?.Path is JArray acePath)
                {
                    loaded.Add((segment, acePath));
                }
                else
                {
                    Logs.Warning(
                        $"VideoStages: clip {clipId} audio segment references AceStepFun track "
                        + $"'{segment.AceStepFunTrack}', which is not present in the workflow; skipping the segment.");
                }
                continue;
            }
            AudioFile file = EmbeddedMediaMaterializer.MaterializeAudio(g, segment.UploadedMedia);
            if (file is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(file, SegmentUploadPlaceholder);
            loaded.Add((segment, new JArray(loadNodeId, 0)));
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

        foreach ((AudioSegmentItemPlan segment, JArray path) in loaded)
        {
            INodeOutput source = bridge.ResolvePath(path);
            if (source is null)
            {
                continue;
            }
            INodeOutput placed = PlaceSegment(bridge, source, segment);
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

    private static INodeOutput PlaceSegment(
        WorkflowBridge bridge,
        INodeOutput source,
        AudioSegmentItemPlan segment)
    {
        // Pad the source up to trim-start + length first: TrimAudioDuration returns the shorter clip
        // when the source runs out (so the segment's declared window would lock silent bed in the
        // conditioning mask), and it throws outright when the trim start is past the source's end.
        // Padding makes the declared window authoritative: a short source plays, then intended silence.
        SwarmEnsureAudioNode ensure = bridge.AddNode(new SwarmEnsureAudioNode().With(
            TargetDuration: segment.TrimStartSeconds + segment.LengthSeconds));
        ensure.Audio.ConnectToUntyped(source);

        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: segment.TrimStartSeconds,
            Duration: segment.LengthSeconds);
        trim.Audio.ConnectTo(ensure.AUDIO);
        INodeOutput trimmed = ApplyVolume(bridge, trim.AUDIO, segment.Volume);

        if (segment.StartSeconds <= 0)
        {
            return trimmed;
        }

        INodeOutput silence = Silence(bridge, segment.StartSeconds);
        AudioConcatNode concat = bridge.AddNode(new AudioConcatNode()).With(Direction: ConcatDirectionAfter);
        concat.Audio1.ConnectToUntyped(silence);
        concat.Audio2.ConnectToUntyped(trimmed);
        return concat.AUDIO;
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

    private static INodeOutput Silence(WorkflowBridge bridge, double durationSeconds)
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
