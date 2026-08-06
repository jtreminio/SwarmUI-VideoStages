using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed record MiniMaxReferencePlan(
    ClipReferenceKind Kind,
    string Source,
    UploadedMediaSpec Media,
    bool IncludeSoundtrack,
    double MediaScale);

internal static class MiniMaxClipReferences
{
    internal const int MaxImages = 9;
    internal const int MaxVideos = 3;
    internal const int MaxAudios = 3;

    internal static IReadOnlyList<MiniMaxReferencePlan> Compile(
        ClipSpec clip,
        ICollection<PlanDiagnostic> diagnostics)
    {
        List<MiniMaxReferencePlan> compiled = [];
        void Ignore(string code, string message) =>
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                $"minimax.clip-reference.{code}",
                message,
                clip.Id));
        Dictionary<ClipReferenceKind, int> counts = [];

        foreach (ClipReferenceSpec reference in clip.References ?? [])
        {
            bool uploaded = StringUtils.Equals(reference.Source, "Upload");
            bool hostCapture = reference.Kind == ClipReferenceKind.Image
                && ImageReferenceSyntax.IsHostStageSource(reference.Source);
            if (!uploaded && !hostCapture)
            {
                Ignore(
                    "source-ignored",
                    $"Clip {clip.Id} has a {Label(reference.Kind)} reference from source "
                        + $"'{reference.Source}', which MiniMax H3 reference conditioning cannot "
                        + "read. The authored reference remains saved and is ignored for this "
                        + "generation.");
                continue;
            }
            if (uploaded && string.IsNullOrWhiteSpace(reference.Media?.Data))
            {
                Ignore(
                    "media-missing",
                    $"Clip {clip.Id} has an uploaded {Label(reference.Kind)} reference with no "
                        + "media; it is ignored for this generation.");
                continue;
            }
            int used = counts.GetValueOrDefault(reference.Kind);
            int limit = Limit(reference.Kind);
            if (used >= limit)
            {
                Ignore(
                    "limit-exceeded",
                    $"Clip {clip.Id} has more than {limit} {Label(reference.Kind)} references. "
                        + $"MiniMax H3 accepts {limit}; the extra ones remain saved and are "
                        + "ignored for this generation.");
                continue;
            }
            counts[reference.Kind] = used + 1;
            bool isVideo = reference.Kind == ClipReferenceKind.Video;
            compiled.Add(new(
                reference.Kind,
                reference.Source?.Trim() ?? "",
                reference.Media,
                isVideo && reference.IncludeSoundtrack,
                isVideo ? reference.MediaScale : 1));
        }
        return compiled;
    }

    internal static IEnumerable<PlanDiagnostic> PreflightUploads(
        UploadedMediaPreflight media,
        VideoExecutionPlan plan)
    {
        foreach (ClipPlan clip in plan.Clips)
        {
            if (clip.ArchitecturePayload is not MiniMaxClipPayload payload)
            {
                continue;
            }
            foreach (MiniMaxReferencePlan reference in payload.References)
            {
                if (!StringUtils.Equals(reference.Source, "Upload"))
                {
                    continue;
                }
                string descriptor = $"clip {clip.ClipId} {Label(reference.Kind)} reference";
                PlanDiagnostic unreadable = reference.Kind switch
                {
                    ClipReferenceKind.Image => media.ImageDiagnostic(
                        reference.Media.Data,
                        reference.Media.FileName,
                        descriptor,
                        clip.ClipId),
                    ClipReferenceKind.Audio => media.AudioDiagnostic(
                        reference.Media.Data,
                        reference.Media.FileName,
                        clip.ClipId),
                    _ => media.VideoDiagnostic(
                        reference.Media.Data,
                        reference.Media.FileName,
                        descriptor,
                        clip.ClipId),
                };
                if (unreadable is not null)
                {
                    yield return unreadable;
                }
            }
        }
    }

    internal static string Label(ClipReferenceKind kind) => kind switch
    {
        ClipReferenceKind.Image => "image",
        ClipReferenceKind.Video => "video",
        _ => "audio",
    };

    private static int Limit(ClipReferenceKind kind) => kind switch
    {
        ClipReferenceKind.Image => MaxImages,
        ClipReferenceKind.Video => MaxVideos,
        _ => MaxAudios,
    };
}
