using SwarmUI.Text2Image;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Turns unreadable uploaded media into blocking diagnostics before graph mutation begins, so the
/// throwing Get* entry points can treat a load failure as a bug. Runtime loads the same payloads a
/// second time; for a server-side path that is a second disk read.
/// </summary>
internal sealed class UploadedMediaPreflight(T2IParamInput input)
{
    private const string UnreadableCode = "media-unreadable";

    internal IReadOnlyList<PlanDiagnostic> Preflight(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<PlanDiagnostic> diagnostics = [];
        foreach (ClipPlan clip in plan.Clips)
        {
            if (!UploadedMedia.TryGetInitVideo(
                input, clip.InitVideo, out _, out string videoError))
            {
                diagnostics.Add(Unreadable(videoError, clip.ClipId));
            }
            AddAudioDiagnostic(diagnostics, clip.Audio?.Base?.UploadedMedia, clip.ClipId);
            foreach (AudioSpanPlan span in clip.Audio?.Spans ?? [])
            {
                AddAudioDiagnostic(diagnostics, span.UploadedMedia, clip.ClipId);
            }
        }
        return diagnostics.AsReadOnly();
    }

    /// <summary>Null when the audio is readable or absent.</summary>
    internal PlanDiagnostic AudioDiagnostic(
        string data,
        string fileName,
        int clipId,
        int? stageId = null) =>
        UploadedMedia.TryGetAudio(input, data, fileName, out _, out string error)
            ? null
            : Unreadable(error, clipId, stageId);

    /// <summary>Null when the video is readable or absent.</summary>
    internal PlanDiagnostic VideoDiagnostic(
        string data,
        string fileName,
        string descriptor,
        int clipId,
        int? stageId = null) =>
        UploadedMedia.TryGetVideo(input, data, fileName, descriptor, out _, out string error)
            ? null
            : Unreadable(error, clipId, stageId);

    /// <summary>Null when the reference image is readable.</summary>
    internal PlanDiagnostic ImageDiagnostic(
        string inlineData,
        string uploadFileName,
        string descriptor,
        int clipId,
        int? stageId = null) =>
        UploadedMedia.TryGetRefImage(
            input, inlineData, uploadFileName, descriptor, out _, out string error)
            ? null
            : Unreadable(error, clipId, stageId);

    private void AddAudioDiagnostic(
        List<PlanDiagnostic> diagnostics,
        UploadedMediaSpec media,
        int clipId)
    {
        if (AudioDiagnostic(media?.Data, media?.FileName, clipId) is { } unreadable)
        {
            diagnostics.Add(unreadable);
        }
    }

    private static PlanDiagnostic Unreadable(string error, int clipId, int? stageId = null) =>
        new(PlanDiagnosticSeverity.Error, UnreadableCode, error, clipId, stageId);
}
