using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Orders projected audio span windows and reports their base-track requirement.</summary>
internal static class AudioSpanPlanCompiler
{
    private const string SpansWithoutBase = "audio.spans.preserve_windowed_no_base";
    private const string SpanIgnoredNoSource = "audio.span.ignored_no_source";
    private const string SpanIgnoredInvalidWindow = "audio.span.ignored_invalid_window";

    internal static AudioSpanCompilation Compile(
        IEnumerable<AudioSpanPlan> spans,
        AudioBaseSourcePlan baseSource)
    {
        ImmutableArray<PlanDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        ImmutableArray<AudioSpanPlan>.Builder items = ImmutableArray.CreateBuilder<AudioSpanPlan>();
        foreach (AudioSpanPlan span in spans ?? [])
        {
            if (span is null)
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning, SpanIgnoredNoSource, "An audio track has no source and was ignored."));
                continue;
            }
            if (double.IsNaN(span.StartSeconds) || double.IsInfinity(span.StartSeconds)
                || double.IsNaN(span.TrimStartSeconds) || double.IsInfinity(span.TrimStartSeconds)
                || double.IsNaN(span.LengthSeconds) || double.IsInfinity(span.LengthSeconds)
                || span.StartSeconds < 0 || span.TrimStartSeconds < 0 || span.LengthSeconds <= 0)
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning, SpanIgnoredInvalidWindow, "An audio track has an invalid time window and was ignored."));
                continue;
            }
            // Spans are only executable from these two sources; the shared vocabulary carries
            // more kinds than a projected span can resolve.
            if (span.SourceKind is not (AudioSourceKind.AceStepFun or AudioSourceKind.Upload))
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning, SpanIgnoredNoSource, "An audio track has an unusable source kind and was ignored."));
                continue;
            }
            if (span.SourceKind == AudioSourceKind.AceStepFun
                && span.AceStepFunTrack is null)
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning, SpanIgnoredNoSource, "An audio track has no usable AceStepFun source and was ignored."));
                continue;
            }
            if (span.SourceKind == AudioSourceKind.Upload
                && string.IsNullOrWhiteSpace(span.UploadedMedia?.Data))
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning, SpanIgnoredNoSource, "An audio track has no usable upload source and was ignored."));
                continue;
            }
            items.Add(span);
        }

        ImmutableArray<AudioSpanPlan> ordered = items
            .OrderBy(item => item.StartSeconds)
            .ThenBy(item => item.TrimStartSeconds)
            .ToImmutableArray();
        if (!ordered.IsEmpty && !baseSource.HasConfiguredTrack)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                SpansWithoutBase,
                "Audio tracks have no locked base audio, so only their windows are preserved and gaps are generated."));
        }
        return new(ordered, diagnostics.ToImmutable());
    }
}

internal sealed record AudioSpanCompilation(
    ImmutableArray<AudioSpanPlan> Spans,
    ImmutableArray<PlanDiagnostic> Diagnostics);
