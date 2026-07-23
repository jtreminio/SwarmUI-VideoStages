using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Converts parsed clip configuration into immutable audio policy. It intentionally does not inspect
/// the workflow: whether a native track, an AceStepFun decode, or a ControlNet capture exists is a
/// runtime artifact-resolution concern, not a planning concern.
/// </summary>
internal static class AudioPlanCompiler
{
    private const string UnknownSourceDefaultsToNative = "audio.source.unknown_defaults_to_native";
    private const string ControlNetOverridesAudioLength = "audio.length.controlnet_overrides_audio";
    private const string AudioLengthWithoutTrack = "audio.length.audio_owner_has_no_lockable_track";
    private const string VoiceReferenceMissingSample = "audio.voice_reference.missing_sample";
    private const string DriveVoiceReferenceMissingMedia =
        "audio.voice_reference.drive_media_missing";
    private const string SegmentsWithoutBase = "audio.segments.preserve_windowed_no_base";
    private const string ReuseNeedsThreeStages = "audio.reuse.requires_three_stages";
    private const string SegmentIgnoredNoSource = "audio.segment.ignored_no_source";
    private const string SegmentIgnoredInvalidWindow = "audio.segment.ignored_invalid_window";

    public static AudioPlan Compile(ClipSpec clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<AudioPlanDiagnostic>();
        AudioBaseSourcePlan baseSource = CompileBaseSource(clip, diagnostics);
        AudioVoiceReferencePlan voiceReference = CompileVoiceReference(clip, diagnostics);
        AudioLengthPlan length = CompileLength(clip, baseSource, diagnostics);
        AudioSegmentPlan segments = CompileSegments(clip, baseSource, diagnostics);
        AudioReusePlan reuse = CompileReuse(clip, diagnostics);

        return new AudioPlan(baseSource, voiceReference, length, segments, reuse, diagnostics.ToImmutable());
    }

    private static AudioBaseSourcePlan CompileBaseSource(
        ClipSpec clip,
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics)
    {
        string raw = (clip.AudioSource ?? Constants.AudioSourceNative).Trim();
        if (raw.Length == 0 || StringUtils.Equals(raw, Constants.AudioSourceNative))
        {
            return new(AudioBaseSourceKind.Native, raw, null, HasConfiguredTrack: true, null);
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceVoiceRef))
        {
            return new(AudioBaseSourceKind.None, raw, null, HasConfiguredTrack: false, null);
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceUpload))
        {
            return new(
                AudioBaseSourceKind.Upload,
                raw,
                null,
                HasConfiguredTrack: !string.IsNullOrWhiteSpace(clip.UploadedAudio?.Data),
                CompileMedia(clip.UploadedAudio));
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceControlNet))
        {
            return new(AudioBaseSourceKind.ControlNet, raw, null, HasConfiguredTrack: true, null);
        }
        if (AudioHandler.TryParseAceStepFunAudioSource(raw, out int track))
        {
            return new(AudioBaseSourceKind.AceStepFun, raw, track, HasConfiguredTrack: true, null);
        }

        diagnostics.Add(new(
            UnknownSourceDefaultsToNative,
            $"Audio source '{raw}' is not a supported external source, so it follows the native-audio path."));
        return new(AudioBaseSourceKind.Native, raw, null, HasConfiguredTrack: true, null);
    }

    private static AudioVoiceReferencePlan CompileVoiceReference(
        ClipSpec clip,
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics)
    {
        IReadOnlyList<IcLoraSpec> icLoras = clip.IcLoras ?? [];
        int driveIndex = -1;
        for (int i = 0; i < icLoras.Count; i++)
        {
            if (icLoras[i].DriveAudioRef)
            {
                driveIndex = i;
                break;
            }
        }
        IcLoraSpec drive = driveIndex >= 0 ? icLoras[driveIndex] : null;
        bool sourceRequestsVoiceRef = StringUtils.Equals(clip.AudioSource, Constants.AudioSourceVoiceRef);
        bool requested = drive is not null || sourceRequestsVoiceRef;
        if (drive is not null)
        {
            AudioMediaIdentityPlan media = CompileMedia(drive.Video);
            bool hasMedia = !string.IsNullOrWhiteSpace(media?.Data);
            if (!hasMedia)
            {
                diagnostics.Add(new(
                    DriveVoiceReferenceMissingMedia,
                    $"IC-LoRA entry {driveIndex} requests drive-audio voice reference but has no uploaded drive media."));
            }
            return new(
                AudioVoiceReferenceKind.IcLoraDriveVideo,
                IsRequested: true,
                HasConfiguredSample: hasMedia,
                media,
                driveIndex);
        }
        if (!sourceRequestsVoiceRef)
        {
            return new(
                AudioVoiceReferenceKind.None,
                IsRequested: false,
                HasConfiguredSample: false,
                null,
                null);
        }

        bool hasUpload = !string.IsNullOrWhiteSpace(clip.UploadedAudio?.Data);
        if (!hasUpload)
        {
            diagnostics.Add(new(
                VoiceReferenceMissingSample,
                "Voice Reference is selected but this clip has no uploaded voice-reference audio."));
        }
        return new(
            AudioVoiceReferenceKind.ClipUpload,
            requested,
            hasUpload,
            CompileMedia(clip.UploadedAudio),
            null);
    }

    private static AudioLengthPlan CompileLength(
        ClipSpec clip,
        AudioBaseSourcePlan baseSource,
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics)
    {
        AudioLengthOwner owner;
        if (clip.ClipLengthFromControlNet)
        {
            owner = AudioLengthOwner.ControlNet;
            if (clip.ClipLengthFromAudio)
            {
                diagnostics.Add(new(
                    ControlNetOverridesAudioLength,
                    "ControlNet owns clip length when both ControlNet and audio length are requested."));
            }
        }
        else if (clip.ClipLengthFromAudio)
        {
            owner = AudioLengthOwner.Audio;
            if (!baseSource.HasConfiguredTrack)
            {
                diagnostics.Add(new(
                    AudioLengthWithoutTrack,
                    "Audio owns clip length, but the selected audio source does not provide a locked track."));
            }
        }
        else
        {
            owner = AudioLengthOwner.Timeline;
        }

        // Preserve the existing compatibility behavior while separating it from the user-facing
        // duration owner above. The coordinator's non-handoff injection matches a native source to
        // video length even without the checkbox; the root-handoff path only does so for an external
        // source with the checkbox. The executor can retire this asymmetry deliberately later, but
        // it must not rediscover it from graph state today.
        bool external = baseSource.Kind is AudioBaseSourceKind.Upload
            or AudioBaseSourceKind.AceStepFun
            or AudioBaseSourceKind.ControlNet;
        bool nonHandoffMatches = external ? clip.ClipLengthFromAudio : true;
        bool rootHandoffMatches = external && clip.ClipLengthFromAudio;
        return new(
            owner,
            clip.ClipLengthFromAudio,
            clip.ClipLengthFromControlNet,
            nonHandoffMatches,
            rootHandoffMatches);
    }

    private static AudioSegmentPlan CompileSegments(
        ClipSpec clip,
        AudioBaseSourcePlan baseSource,
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics)
    {
        ImmutableArray<AudioSegmentItemPlan>.Builder items = ImmutableArray.CreateBuilder<AudioSegmentItemPlan>();
        foreach (AudioSegmentSpec segment in clip.AudioSegments ?? [])
        {
            if (segment is null)
            {
                diagnostics.Add(new(SegmentIgnoredNoSource, "An audio segment has no source and was ignored."));
                continue;
            }
            if (double.IsNaN(segment.StartSeconds) || double.IsInfinity(segment.StartSeconds)
                || double.IsNaN(segment.TrimStartSeconds) || double.IsInfinity(segment.TrimStartSeconds)
                || double.IsNaN(segment.LengthSeconds) || double.IsInfinity(segment.LengthSeconds)
                || segment.StartSeconds < 0 || segment.TrimStartSeconds < 0 || segment.LengthSeconds <= 0)
            {
                diagnostics.Add(new(SegmentIgnoredInvalidWindow, "An audio segment has an invalid time window and was ignored."));
                continue;
            }
            if (AudioHandler.TryParseAceStepFunAudioSource(segment.AceStepFunSource, out int aceTrack))
            {
                items.Add(new(AudioSegmentSourceKind.AceStepFun, aceTrack,
                    segment.StartSeconds, segment.TrimStartSeconds, segment.LengthSeconds, null));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(segment.Source?.Data))
            {
                items.Add(new(AudioSegmentSourceKind.Upload, null,
                    segment.StartSeconds, segment.TrimStartSeconds, segment.LengthSeconds,
                    CompileMedia(segment.Source)));
                continue;
            }
            diagnostics.Add(new(SegmentIgnoredNoSource, "An audio segment has no usable upload or AceStepFun source and was ignored."));
        }

        ImmutableArray<AudioSegmentItemPlan> ordered = items
            .OrderBy(item => item.StartSeconds)
            .ThenBy(item => item.TrimStartSeconds)
            .ToImmutableArray();
        if (ordered.IsEmpty)
        {
            return new(
                AudioSegmentMode.None,
                AudioSegmentBaseResolutionRequirement.NotRequired,
                ordered);
        }

        if (!baseSource.HasConfiguredTrack)
        {
            diagnostics.Add(new(
                SegmentsWithoutBase,
                "Audio segments have no locked base track, so only their windows are preserved and gaps are generated."));
            return new(
                AudioSegmentMode.PreserveWindowedNoBase,
                AudioSegmentBaseResolutionRequirement.NoBaseConfigured,
                ordered);
        }

        // Do not use HasConfiguredTrack as proof that a base audio artifact exists. Native attached
        // audio, ControlNet capture, and AceStepFun decode are all resolved from the workflow later;
        // a missing one must change execution to PreserveWindowedNoBase rather than lock silent gaps.
        return new(
            AudioSegmentMode.MixOverBase,
            AudioSegmentBaseResolutionRequirement.ResolveAtExecution,
            ordered);
    }

    private static AudioReusePlan CompileReuse(
        ClipSpec clip,
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics)
    {
        int stageCount = clip.Stages?.Count ?? 0;
        bool eligible = clip.ReuseAudio && stageCount >= 3;
        if (clip.ReuseAudio && !eligible)
        {
            diagnostics.Add(new(
                ReuseNeedsThreeStages,
                "Audio reuse needs at least three active stages: generate, capture, then reuse."));
        }
        return new(clip.ReuseAudio, eligible, CaptureStageIndex: 1, ReuseFromStageIndex: 2);
    }

    private static AudioMediaIdentityPlan CompileMedia(UploadedAudioSpec media) => media is null
        ? null
        : new AudioMediaIdentityPlan(media.Data, media.FileName);
}
