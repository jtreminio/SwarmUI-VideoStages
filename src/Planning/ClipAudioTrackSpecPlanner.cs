using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Expands each clip's base audio plan into an explicit one-clip timeline track spec.</summary>
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
                        clip.Audio.Base.Kind,
                        clip.Audio.Base.UploadedMedia?.FileName ?? clip.Audio.Base.RawSource,
                        clip.Audio.Base.UploadedMedia),
                    [new AudioTrackSpanSpec(FirstClipId: clip.ClipId, LastClipId: clip.ClipId)]));
            }
        }
        return tracks.ToImmutable();
    }
}
