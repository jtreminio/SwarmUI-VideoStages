using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;

namespace VideoStages;

/// <summary>
/// Combines a clip's optional overlay audio segments with its base audio, before the cross-clip merge.
/// Tier 1: each segment is trimmed (<see cref="TrimAudioDurationNode"/>, seconds), offset by prepending
/// silence (<see cref="EmptyAudioNode"/> + <see cref="AudioConcatNode"/>), then mixed additively over the
/// running base with <see cref="AudioMergeNode"/> (<c>merge_method="add"</c>, which pads/trims the overlay
/// to the base length). A clip with no segments returns its base audio untouched — the pure existing graph
/// is preserved (regression lock).
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
    /// </summary>
    public WGNodeData Combine(ClipSpec clip, WGNodeData baseAudio, double clipDurationSeconds)
    {
        IReadOnlyList<AudioSegmentSpec> segments = clip?.AudioSegments;
        if (segments is null || segments.Count == 0)
        {
            return baseAudio;
        }

        // Materialize the segment uploads into load nodes BEFORE opening the bridge — CreateAudioLoadNode
        // writes to g.Workflow directly.
        List<(AudioSegmentSpec Spec, JArray Path)> loaded = [];
        foreach (AudioSegmentSpec seg in segments)
        {
            AudioFile file = VideoStagesSpecParser.MaterializeUploadedAudio(g, seg.Source);
            if (file is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(file, SegmentUploadPlaceholder);
            loaded.Add((seg, new JArray(loadNodeId, 0)));
        }
        if (loaded.Count == 0)
        {
            return baseAudio;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);

        INodeOutput accumulator = bridge.ResolvePath(baseAudio?.Path);
        if (accumulator is null && clipDurationSeconds > 0)
        {
            accumulator = Silence(bridge, clipDurationSeconds);
        }

        foreach ((AudioSegmentSpec seg, JArray path) in loaded)
        {
            INodeOutput source = bridge.ResolvePath(path);
            if (source is null)
            {
                continue;
            }
            INodeOutput placed = PlaceSegment(bridge, source, seg);
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

    private static INodeOutput PlaceSegment(WorkflowBridge bridge, INodeOutput source, AudioSegmentSpec seg)
    {
        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: seg.TrimStartSeconds,
            Duration: seg.LengthSeconds);
        trim.Audio.ConnectToUntyped(source);
        bridge.SyncNode(trim);
        INodeOutput trimmed = trim.AUDIO;

        if (seg.StartSeconds <= 0)
        {
            return trimmed;
        }

        INodeOutput silence = Silence(bridge, seg.StartSeconds);
        AudioConcatNode concat = bridge.AddNode(new AudioConcatNode()).With(Direction: ConcatDirectionAfter);
        concat.Audio1.ConnectToUntyped(silence);
        concat.Audio2.ConnectToUntyped(trimmed);
        bridge.SyncNode(concat);
        return concat.AUDIO;
    }

    private static INodeOutput Silence(WorkflowBridge bridge, double durationSeconds)
    {
        EmptyAudioNode empty = bridge.AddNode(new EmptyAudioNode()).With(
            Duration: durationSeconds,
            SampleRate: SilenceSampleRate,
            Channels: SilenceChannels);
        bridge.SyncNode(empty);
        return empty.AUDIO;
    }

    private static INodeOutput Merge(WorkflowBridge bridge, INodeOutput baseAudio, INodeOutput overlay)
    {
        AudioMergeNode merge = bridge.AddNode(new AudioMergeNode()).With(MergeMethod: MergeMethodAdd);
        merge.Audio1.ConnectToUntyped(baseAudio);
        merge.Audio2.ConnectToUntyped(overlay);
        bridge.SyncNode(merge);
        return merge.AUDIO;
    }
}
