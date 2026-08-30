using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed record MiniMaxReferencePlan(
    string Id,
    ClipReferenceKind Kind,
    string Source,
    UploadedMediaSpec Media,
    bool IncludeSoundtrack,
    double MediaScale,
    double StartSeconds,
    double LengthSeconds);

internal static class MiniMaxClipReferences
{
    internal const int MaxImages = 9;
    internal const int MaxVideos = 3;
    internal const int MaxAudios = 3;

    internal static IReadOnlyList<MiniMaxReferencePlan> Compile(
        ClipSpec clip,
        ICollection<PlanDiagnostic> diagnostics,
        bool hasPreviousClipOutput = false) =>
        Compile(clip, clip.References ?? [], diagnostics, hasPreviousClipOutput);

    internal static IReadOnlyList<MiniMaxReferencePlan> CompileSelected(
        ClipSpec clip,
        IReadOnlyList<string> ids,
        ICollection<PlanDiagnostic> diagnostics)
    {
        Dictionary<string, ClipReferenceSpec> byId = (clip.References ?? [])
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Id))
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        List<ClipReferenceSpec> selected = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in ids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id)
                && seen.Add(id)
                && byId.TryGetValue(id, out ClipReferenceSpec reference))
            {
                selected.Add(reference);
            }
        }
        return Compile(clip, selected, diagnostics);
    }

    private static IReadOnlyList<MiniMaxReferencePlan> Compile(
        ClipSpec clip,
        IEnumerable<ClipReferenceSpec> references,
        ICollection<PlanDiagnostic> diagnostics,
        bool hasPreviousClipOutput = false)
    {
        List<MiniMaxReferencePlan> compiled = [];
        void Ignore(string code, string message) =>
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                $"minimax.clip-reference.{code}",
                message,
                clip.Id));
        Dictionary<ClipReferenceKind, int> counts = [];

        foreach (ClipReferenceSpec reference in references)
        {
            bool uploaded = StringUtils.Equals(reference.Source, MediaSource.Upload);
            bool hostCapture = reference.Kind == ClipReferenceKind.Image
                && MediaSource.IsHostStageSource(reference.Source);
            bool controlNet = MediaSource.TryParseControlNetIndex(reference.Source, out _);
            bool aceStepFun = reference.Kind == ClipReferenceKind.Audio
                && MediaSource.TryParseAceStepFunIndex(reference.Source, out _);
            bool previousClip = hasPreviousClipOutput
                && reference.Kind == ClipReferenceKind.Video
                && StringUtils.Equals(reference.Source, MediaSource.PreviousClip);
            if (!uploaded && !hostCapture && !controlNet && !aceStepFun && !previousClip)
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
            bool isTimed = reference.Kind is ClipReferenceKind.Video or ClipReferenceKind.Audio;
            compiled.Add(new(
                reference.Id,
                reference.Kind,
                reference.Source?.Trim() ?? "",
                reference.Media,
                isVideo && reference.IncludeSoundtrack,
                isVideo ? reference.MediaScale : ReferenceScale.Full,
                isTimed ? reference.StartSeconds : 0,
                isTimed ? reference.LengthSeconds : 0));
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
                if (!StringUtils.Equals(reference.Source, MediaSource.Upload))
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
