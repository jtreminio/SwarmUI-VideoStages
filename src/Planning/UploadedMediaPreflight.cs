using SwarmUI.Text2Image;
using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>
/// Proves every uploaded media payload in a compiled plan is loadable before graph mutation begins,
/// so an unreadable upload blocks the request instead of being dropped at runtime. Runtime loads the
/// same payloads a second time; for a server-side path that is a second disk read.
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
            Audio(diagnostics, clip.Audio?.Base?.UploadedMedia, clip.ClipId);
            foreach (AudioSegmentItemPlan segment in clip.Audio?.Segments?.Items ?? [])
            {
                Audio(diagnostics, segment.UploadedMedia, clip.ClipId);
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

    private void Audio(List<PlanDiagnostic> diagnostics, UploadedMediaSpec media, int clipId)
    {
        if (AudioDiagnostic(media?.Data, media?.FileName, clipId) is { } unreadable)
        {
            diagnostics.Add(unreadable);
        }
    }

    private static PlanDiagnostic Unreadable(string error, int clipId, int? stageId = null) =>
        new(PlanDiagnosticSeverity.Error, UnreadableCode, error, clipId, stageId);
}
