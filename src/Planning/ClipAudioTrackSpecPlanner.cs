using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Expands existing clip-local audio plans into explicit one-clip timeline track specs.</summary>
internal static class ClipAudioTrackSpecPlanner
{
    internal static ImmutableArray<AudioTrackSpec> Compile(VideoExecutionPlan videoPlan)
    {
        ImmutableArray<AudioTrackSpec>.Builder tracks = ImmutableArray.CreateBuilder<AudioTrackSpec>();
        for (int index = 0; index < videoPlan.Clips.Count; index++)
        {
            ClipPlan clip = videoPlan.Clips[index];
            if (clip.Audio.Base.HasConfiguredTrack)
            {
                tracks.Add(new(
                    $"clip-{clip.ClipId}-base",
                    new(
                        MapBaseSourceKind(clip.Audio.Base.Kind),
                        clip.Audio.Base.UploadedMedia?.FileName ?? clip.Audio.Base.RawSource,
                        clip.Audio.Base.UploadedMedia),
                    [new AudioTrackSpanSpec(FirstClipId: clip.ClipId, LastClipId: clip.ClipId)]));
            }

            for (int segmentIndex = 0; segmentIndex < clip.Audio.Segments.Items.Length; segmentIndex++)
            {
                AudioSegmentItemPlan segment = clip.Audio.Segments.Items[segmentIndex];
                tracks.Add(new(
                    $"clip-{clip.ClipId}-segment-{segmentIndex}",
                    new(
                        segment.SourceKind == AudioSegmentSourceKind.AceStepFun
                            ? AudioTimelineTrackSourceKind.AceStepFun
                            : AudioTimelineTrackSourceKind.Upload,
                        segment.SourceKind == AudioSegmentSourceKind.AceStepFun
                            ? $"audio{segment.AceStepFunTrack}"
                            : segment.UploadedMedia?.FileName
                                ?? $"clip-{clip.ClipId}-segment-{segmentIndex}",
                        segment.UploadedMedia),
                    [new AudioTrackSpanSpec(
                        FirstClipId: clip.ClipId,
                        LastClipId: clip.ClipId,
                        SourceStartSeconds: segment.TrimStartSeconds,
                        ClipStartOffsetSeconds: segment.StartSeconds,
                        ClipLengthSeconds: segment.LengthSeconds)],
                    segment.Volume));
            }
        }
        return tracks.ToImmutable();
    }

    private static AudioTimelineTrackSourceKind MapBaseSourceKind(AudioBaseSourceKind kind) => kind switch
    {
        AudioBaseSourceKind.Upload => AudioTimelineTrackSourceKind.Upload,
        AudioBaseSourceKind.AceStepFun => AudioTimelineTrackSourceKind.AceStepFun,
        AudioBaseSourceKind.ControlNet => AudioTimelineTrackSourceKind.ControlNet,
        AudioBaseSourceKind.Native => AudioTimelineTrackSourceKind.Native,
        _ => AudioTimelineTrackSourceKind.External,
    };
}
