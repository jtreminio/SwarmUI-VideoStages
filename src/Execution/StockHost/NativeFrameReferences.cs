using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Planning;
using Image = SwarmUI.Utils.Image;

namespace VideoStages.Execution.StockHost;

/// <summary>An authored image reference bound for a model's native first/last frame input.</summary>
internal sealed record NativeFrameReferencePlan(
    string Source,
    string UploadFileName,
    string InlineData);

/// <summary>Implemented by every clip payload whose architecture conditions on native keyframes.</summary>
internal interface INativeFrameReferenceClipPayload
{
    NativeFrameReferencePlan FirstFrameReference { get; }
    NativeFrameReferencePlan LastFrameReference { get; }
}

/// <summary>
/// Shared compilation and materialization for architectures whose host graph conditions on a
/// first and/or final frame image rather than on arbitrary mid-clip references.
/// </summary>
internal static class NativeFrameReferences
{
    internal static (NativeFrameReferencePlan First, NativeFrameReferencePlan Last) Compile(
        ClipSpec clip,
        IReadOnlyList<StageSpec> activeStages,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        VideoArchitectureDescriptor architecture,
        ICollection<PlanDiagnostic> diagnostics,
        string codeToken,
        string label,
        bool allowHostStageSources = false)
    {
        NativeFrameReferencePlan first = null;
        NativeFrameReferencePlan last = null;
        void Ignore(string code, string message) =>
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                $"effective-request.{codeToken}-{code}",
                message,
                clip.Id));
        bool Declares(StageSpec stage, string position, out string modelName)
        {
            ResolvedVideoModel model = stage is null
                ? null
                : stageModels.GetValueOrDefault(stage.ClipStageRawIndex);
            modelName = model?.ModelName ?? "<missing>";
            return model?.ReferencePositions?.Contains(position, StringComparer.Ordinal) == true;
        }

        foreach (FrameRefSpec reference in clip.FrameRefs ?? [])
        {
            bool isFirst = !reference.FromEnd && reference.Frame == 1;
            bool isLast = reference.FromEnd && reference.Frame == 1;
            if (!isFirst && !isLast)
            {
                Ignore(
                    "middle-frame-reference-ignored",
                    $"Clip {clip.Id} has a {label} image reference at "
                        + $"{(reference.FromEnd ? "end-relative" : "start-relative")} frame "
                        + $"{reference.Frame}. {label} native conditioning accepts only the first "
                        + "and final frame. The authored reference remains saved and is ignored "
                        + "for this generation.");
                continue;
            }
            if (isFirst && clip.InitVideo is not null)
            {
                Ignore(
                    "init-video-first-frame-reference-ignored",
                    $"Clip {clip.Id} already enters from init-video video, so its separate "
                        + "first-frame reference remains saved and is ignored for this "
                        + "generation.");
                continue;
            }
            if (isFirst
                && !Declares(
                    activeStages.FirstOrDefault(stage =>
                        !ArchitectureStageActivity.IsPassthrough(stage, architecture)),
                    "first",
                    out string firstModel))
            {
                Ignore(
                    "first-frame-reference-ignored",
                    $"Clip {clip.Id}'s first-frame reference is not supported by the selected "
                        + $"first {label} model '{firstModel}'. The authored reference remains "
                        + "saved and is ignored for this generation.");
                continue;
            }
            bool supportedSource = StringUtils.Equals(reference.Source, "Upload")
                || allowHostStageSources
                    && ImageReferenceSyntax.IsHostStageSource(reference.Source);
            if (!supportedSource)
            {
                Ignore(
                    "frame-reference-source-ignored",
                    $"Clip {clip.Id}'s {label} {(isFirst ? "first" : "final")}-frame reference "
                        + $"uses source '{reference.Source}', but the native {label} "
                        + "bounded-reference path currently accepts uploaded images. The authored "
                        + "reference remains saved and is ignored for this generation.");
                continue;
            }
            if (isFirst ? first is not null : last is not null)
            {
                Ignore(
                    "duplicate-frame-reference-ignored",
                    $"Clip {clip.Id} has more than one {label} {(isFirst ? "first" : "last")} "
                        + "frame reference. The first authored reference remains active; later "
                        + "references remain saved and are ignored for this generation.");
                continue;
            }
            if (isLast
                && !Declares(
                    activeStages.LastOrDefault(stage =>
                        !ArchitectureStageActivity.IsPassthrough(stage, architecture)),
                    "last",
                    out string terminalModel))
            {
                Ignore(
                    "last-frame-reference-ignored",
                    $"Clip {clip.Id}'s final-frame reference is not supported by the selected "
                        + $"terminal {label} model '{terminalModel}'. The authored reference "
                        + "remains saved and is ignored for this generation.");
                continue;
            }
            NativeFrameReferencePlan compiled = new(
                reference.Source?.Trim() ?? "",
                reference.UploadFileName,
                reference.Data);
            if (isFirst)
            {
                first = compiled;
            }
            else
            {
                last = compiled;
            }
        }
        return (first, last);
    }

    /// <summary>
    /// Reports every clip whose authored first/last frame upload cannot be loaded, so an unreadable
    /// keyframe blocks the request instead of silently dropping the frame it conditions on.
    /// </summary>
    internal static IEnumerable<PlanDiagnostic> PreflightUploads(
        UploadedMediaPreflight media,
        VideoExecutionPlan plan)
    {
        foreach (ClipPlan clip in plan.Clips)
        {
            if (clip.ArchitecturePayload is not INativeFrameReferenceClipPayload payload)
            {
                continue;
            }
            foreach ((NativeFrameReferencePlan reference, string edge) in
                new[] { (payload.FirstFrameReference, "first"), (payload.LastFrameReference, "last") })
            {
                if (reference is null || !StringUtils.Equals(reference.Source, "Upload"))
                {
                    continue;
                }
                if (media.ImageDiagnostic(
                    reference.InlineData,
                    reference.UploadFileName,
                    $"clip {clip.ClipId} {edge} frame image",
                    clip.ClipId) is { } unreadable)
                {
                    yield return unreadable;
                }
            }
        }
    }

    internal static Image MaterializeUpload(
        WorkflowGenerator generator,
        NativeFrameReferencePlan reference,
        string descriptor)
    {
        if (reference is null)
        {
            return null;
        }
        if (!StringUtils.Equals(reference.Source, "Upload"))
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                generator.UserInput,
                $"VideoStages: {descriptor} source '{reference.Source}' cannot be materialized "
                    + "by the native image input; ignoring it for this generation.");
            return null;
        }
        ImageFile image = UploadedMedia.GetRefImage(
            generator.UserInput,
            reference.InlineData,
            reference.UploadFileName,
            descriptor);
        return image as Image;
    }
}
