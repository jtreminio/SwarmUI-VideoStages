using System.Collections.Immutable;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiles the optional speaker-identity input independently from the base track.</summary>
internal static class AudioVoiceReferencePlanCompiler
{
    private const string VoiceReferenceMissingSample = "audio.voice_reference.missing_sample";
    private const string DriveVoiceReferenceMissingMedia = "audio.voice_reference.drive_media_missing";

    internal static AudioPlanComponentResult<AudioVoiceReferencePlan> Compile(ClipSpec clip)
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
        if (drive is not null)
        {
            AudioMediaIdentityPlan media = AudioMediaIdentityCompiler.Compile(drive.Video);
            bool hasMedia = !string.IsNullOrWhiteSpace(media?.Data);
            ImmutableArray<AudioPlanDiagnostic> diagnostics = hasMedia
                ? []
                : [new(
                    DriveVoiceReferenceMissingMedia,
                    $"IC-LoRA entry {driveIndex} requests drive-audio voice reference but has no uploaded drive media.")];
            return new(
                new(
                    AudioVoiceReferenceKind.IcLoraDriveVideo,
                    IsRequested: true,
                    HasConfiguredSample: hasMedia,
                    media,
                    driveIndex,
                    CompileIcLoraMediaKind(drive.Video?.Data),
                    sourceRequestsVoiceRef ? AudioMediaIdentityCompiler.Compile(clip.UploadedAudio) : null),
                diagnostics);
        }
        if (!sourceRequestsVoiceRef)
        {
            return new(
                new(AudioVoiceReferenceKind.None, false, false, null, null, null, null),
                ImmutableArray<AudioPlanDiagnostic>.Empty);
        }

        bool hasUpload = !string.IsNullOrWhiteSpace(clip.UploadedAudio?.Data);
        return new(
            new(
                AudioVoiceReferenceKind.ClipUpload,
                IsRequested: true,
                HasConfiguredSample: hasUpload,
                AudioMediaIdentityCompiler.Compile(clip.UploadedAudio),
                null,
                null,
                null),
            hasUpload
                ? []
                : [new(
                    VoiceReferenceMissingSample,
                    "Voice Reference is selected but this clip has no uploaded voice-reference audio.")]);
    }

    private static IcLoraUploadedMediaKind CompileIcLoraMediaKind(string data)
    {
        if (data?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return IcLoraUploadedMediaKind.Image;
        }
        if (data?.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return IcLoraUploadedMediaKind.Video;
        }
        return string.IsNullOrWhiteSpace(data)
            ? IcLoraUploadedMediaKind.None
            : IcLoraUploadedMediaKind.Unknown;
    }
}
