namespace VideoStages.Planning;

/// <summary>
/// Turns root timeline windows back into the clip-local segment plans consumed by the existing LTX
/// mixer. Each projected window starts at clip-local time and carries the source offset calculated
/// by <see cref="AudioTimelineTrackSpanProjector"/>.
/// </summary>
internal static class TimelineAudioSegmentPlanProjector
{
    internal static IReadOnlyList<ClipPlan> Apply(
        IReadOnlyList<ClipPlan> clips,
        AudioTimelinePlan timeline,
        IReadOnlySet<string> authoredTrackIds)
    {
        Dictionary<int, AudioTimelineClipWindow> clipWindows =
            timeline.ClipWindows.ToDictionary(window => window.ClipId);
        Dictionary<int, List<AudioSegmentItemPlan>> additions = [];

        foreach (AudioTimelineTrackPlan track in timeline.Tracks)
        {
            if (!authoredTrackIds.Contains(track.TrackId))
            {
                continue;
            }
            foreach (AudioTrackClipWindow window in track.Windows)
            {
                if (!clipWindows.TryGetValue(window.ClipId, out AudioTimelineClipWindow clipWindow)
                    || clipWindow.TimelineStartSeconds is not double clipStart)
                {
                    continue;
                }

                AudioSegmentItemPlan item;
                if (track.Source.Kind == AudioTimelineTrackSourceKind.AceStepFun
                    && AudioHandler.TryParseAceStepFunAudioSource(
                        track.Source.Reference,
                        out int aceStepTrack))
                {
                    item = new(
                        AudioSegmentSourceKind.AceStepFun,
                        aceStepTrack,
                        window.TimelineStartSeconds - clipStart,
                        window.SourceStartSeconds,
                        window.DurationSeconds,
                        null,
                        track.Volume);
                }
                else if (track.Source.Kind == AudioTimelineTrackSourceKind.Upload
                    && track.Source.UploadedMedia is not null)
                {
                    item = new(
                        AudioSegmentSourceKind.Upload,
                        null,
                        window.TimelineStartSeconds - clipStart,
                        window.SourceStartSeconds,
                        window.DurationSeconds,
                        track.Source.UploadedMedia,
                        track.Volume);
                }
                else
                {
                    continue;
                }

                additions.TryAdd(window.ClipId, []);
                additions[window.ClipId].Add(item);
            }
        }

        return Array.AsReadOnly(clips.Select(clip =>
        {
            if (!additions.TryGetValue(clip.ClipId, out List<AudioSegmentItemPlan> projected))
            {
                return clip;
            }
            AudioPlanComponentResult<AudioSegmentPlan> compiled =
                AudioSegmentPlanCompiler.Compile(
                    clip.Audio.Segments.Items.AddRange(projected),
                    clip.Audio.Base);
            return clip with
            {
                Audio = clip.Audio with
                {
                    Segments = compiled.Plan,
                    Diagnostics = clip.Audio.Diagnostics.AddRange(compiled.Diagnostics),
                },
            };
        }).ToArray());
    }
}
