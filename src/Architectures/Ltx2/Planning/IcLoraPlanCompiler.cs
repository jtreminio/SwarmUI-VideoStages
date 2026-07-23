using System.Collections.Immutable;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiles stage-applicable IC-LoRAs and their pure drive-media intent.</summary>
internal static class IcLoraPlanCompiler
{
    internal static IReadOnlyList<VideoPlanDiagnostic> ValidateClip(ClipSpec clip)
    {
        List<VideoPlanDiagnostic> diagnostics = [];
        HashSet<int> authoredStageIndices = clip.AuthoredStages is { Count: > 0 }
            ? [.. clip.AuthoredStages.Select(stage => stage.RawIndex)]
            : [.. (clip.Stages ?? []).Select(stage => stage.ClipStageRawIndex)];
        IReadOnlyList<IcLoraSpec> entries = clip.IcLoras ?? [];
        for (int index = 0; index < entries.Count; index++)
        {
            IcLoraSpec entry = entries[index];
            IcLoraDriveMediaContract contract = IcLoraDriveMediaContracts.Resolve(entry.Preset);
            IcLoraDriveMediaPlan media = CompileDriveMedia(entry.DriveMedia);
            if (entry.Stage >= 0 && !authoredStageIndices.Contains(entry.Stage))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.stage-target-invalid",
                    $"targets authored stage {entry.Stage}, which does not exist"));
            }

            IcLoraVisualGuideSourceKind sourceKind = ResolveSourceKind(entry.Source);
            if (contract.Consumption == IcLoraDriveMediaConsumption.AudioReference)
            {
                if (sourceKind != IcLoraVisualGuideSourceKind.UploadedMedia)
                {
                    diagnostics.Add(Error(
                        clip,
                        index,
                        "ltx2.ic-lora.audio-drive-source-unsupported",
                        "uses an audio drive-media contract but its source is not Upload"));
                }
                if (contract.RequiresUpload && !media.IsConfigured)
                {
                    diagnostics.Add(Error(
                        clip,
                        index,
                        "ltx2.ic-lora.audio-drive-media-missing",
                        "requires uploaded audio or video Drive Media"));
                }
                else if (!contract.Accepts(media.Kind))
                {
                    diagnostics.Add(Error(
                        clip,
                        index,
                        "ltx2.ic-lora.audio-drive-media-unsupported",
                        "requires audio or video Drive Media; images are not speaker samples"));
                }
                if (CompileControlMode(entry.ControlType) != IcLoraControlMode.None)
                {
                    diagnostics.Add(Error(
                        clip,
                        index,
                        "ltx2.ic-lora.audio-drive-control-unsupported",
                        "consumes audio only and cannot use visual control preprocessing"));
                }
                ValidateAutoModel(clip, entry, index, diagnostics);
                continue;
            }

            if (sourceKind == IcLoraVisualGuideSourceKind.Unknown)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.source-unsupported",
                    $"uses unsupported drive source '{entry.Source}'"));
            }
            if (sourceKind == IcLoraVisualGuideSourceKind.StageInput
                && clip.SourceVideo is null
                && (clip.Stages ?? []).Any(stage =>
                    stage.ClipStageIndex == 0
                    && (entry.Stage < 0 || entry.Stage == stage.ClipStageRawIndex)))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.stage-input-unavailable",
                    "uses Stage Input on a generated first stage with no incoming video"));
            }
            if (sourceKind == IcLoraVisualGuideSourceKind.UploadedMedia
                && media.IsConfigured
                && !contract.Accepts(media.Kind))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.upload-kind-unsupported",
                    "requires image or video Drive Media"));
            }
            if (CompileControlMode(entry.ControlType) == IcLoraControlMode.Unknown)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.control-mode-unsupported",
                    $"uses unsupported control mode '{entry.ControlType}'"));
            }
            ValidateAutoModel(clip, entry, index, diagnostics);
        }

        foreach (StageSpec stage in clip.Stages ?? [])
        {
            List<int> audioEntries = [];
            for (int index = 0; index < entries.Count; index++)
            {
                IcLoraSpec entry = entries[index];
                if ((entry.Stage < 0 || entry.Stage == stage.ClipStageRawIndex)
                    && IcLoraDriveMediaContracts.Resolve(entry.Preset).Consumption
                        == IcLoraDriveMediaConsumption.AudioReference)
                {
                    audioEntries.Add(index);
                }
            }
            if (audioEntries.Count > 1)
            {
                diagnostics.Add(new(
                    VideoPlanDiagnosticSeverity.Error,
                    "ltx2.ic-lora.audio-drive-overlap",
                    $"Clip {clip.Id} stage {stage.ClipStageRawIndex} has overlapping audio-consuming "
                        + $"IC-LoRAs ({string.Join(", ", audioEntries)}); use one speaker drive per stage.",
                    clip.Id));
            }
            if (stage.IsPassthrough && audioEntries.Count > 0)
            {
                diagnostics.Add(new(
                    VideoPlanDiagnosticSeverity.Error,
                    "ltx2.ic-lora.audio-drive-passthrough",
                    $"Clip {clip.Id} stage {stage.ClipStageRawIndex} is passthrough, so its audio-consuming "
                        + "IC-LoRA cannot run; target a generating stage.",
                    clip.Id));
            }
        }
        return diagnostics.AsReadOnly();
    }

    internal static ImmutableArray<IcLoraPlan> Compile(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<IcLoraPlan>.Builder plans = ImmutableArray.CreateBuilder<IcLoraPlan>();
        IReadOnlyList<IcLoraSpec> entries = clip.IcLoras ?? [];
        for (int i = 0; i < entries.Count; i++)
        {
            IcLoraSpec entry = entries[i];
            if (entry.Stage >= 0 && entry.Stage != stage.ClipStageRawIndex)
            {
                continue;
            }

            IcLoraDriveMediaContract contract = IcLoraDriveMediaContracts.Resolve(entry.Preset);
            IcLoraDriveMediaPlan driveMedia = CompileDriveMedia(entry.DriveMedia);
            IcLoraVisualGuidePlan visualGuide = CompileVisualGuide(clip, entry, contract, driveMedia);
            double? guideStrength = null;
            if (visualGuide.HasGuide)
            {
                if (stage.ControlNetStrength is double stageStrength)
                {
                    guideStrength = stageStrength;
                }
                else if (visualGuide.Kind != IcLoraVisualGuideSourceKind.ControlNet)
                {
                    guideStrength = 1.0;
                }
            }

            plans.Add(new IcLoraPlan(
                i,
                entry.Lora,
                StringUtils.Equals(entry.Lora, IcLoraWeights.AutoModelToken),
                entry.Preset,
                entry.Strength,
                entry.AttentionStrength,
                CompileControlMode(entry.ControlType),
                contract,
                driveMedia,
                visualGuide,
                guideStrength));
        }
        return plans.ToImmutable();
    }

    private static IcLoraVisualGuidePlan CompileVisualGuide(
        ClipSpec clip,
        IcLoraSpec entry,
        IcLoraDriveMediaContract contract,
        IcLoraDriveMediaPlan driveMedia)
    {
        string raw = NormalizeSource(entry.Source);
        if (contract.Consumption == IcLoraDriveMediaConsumption.AudioReference)
        {
            return new(
                IcLoraVisualGuideSourceKind.LoaderOnly,
                raw,
                null,
                HasGuide: false);
        }

        IcLoraVisualGuideSourceKind kind = ResolveSourceKind(raw);
        if (kind == IcLoraVisualGuideSourceKind.UploadedMedia)
        {
            if (driveMedia.IsConfigured)
            {
                return new(kind, raw, null, HasGuide: true);
            }
            if (clip.SourceVideo is not null)
            {
                return new(
                    IcLoraVisualGuideSourceKind.SourcedClipInput,
                    raw,
                    null,
                    HasGuide: true);
            }
            return new(
                IcLoraVisualGuideSourceKind.LoaderOnly,
                raw,
                null,
                HasGuide: false);
        }
        if (kind == IcLoraVisualGuideSourceKind.StageInput)
        {
            return new(kind, raw, null, HasGuide: true);
        }
        if (kind == IcLoraVisualGuideSourceKind.ControlNet
            && ControlNetSourcePlan.TryParseIndex(raw, out int controlNetIndex))
        {
            return new(kind, raw, controlNetIndex, HasGuide: true);
        }
        return new(
            IcLoraVisualGuideSourceKind.Unknown,
            raw,
            null,
            HasGuide: false);
    }

    private static IcLoraControlMode CompileControlMode(string controlType)
    {
        string compact = StringUtils.Compact(controlType);
        if (compact.Length == 0
            || StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraControlNone)))
        {
            return IcLoraControlMode.None;
        }
        if (StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraControlCanny)))
        {
            return IcLoraControlMode.Canny;
        }
        if (StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraControlDepth)))
        {
            return IcLoraControlMode.Depth;
        }
        if (StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraControlNormal)))
        {
            return IcLoraControlMode.Normal;
        }
        return IcLoraControlMode.Unknown;
    }

    internal static int? ResolvePrimaryControlNetSourceIndex(ClipSpec clip)
    {
        foreach (IcLoraSpec entry in clip.IcLoras ?? [])
        {
            if (ControlNetSourcePlan.TryParseIndex(
                NormalizeSource(entry.Source),
                out int sourceIndex))
            {
                return sourceIndex;
            }
        }
        return null;
    }

    private static IcLoraVisualGuideSourceKind ResolveSourceKind(string source)
    {
        string normalized = NormalizeSource(source);
        if (StringUtils.Equals(normalized, Constants.IcLoraSourceUpload))
        {
            return IcLoraVisualGuideSourceKind.UploadedMedia;
        }
        if (StringUtils.Equals(normalized, Constants.IcLoraSourceStageInput))
        {
            return IcLoraVisualGuideSourceKind.StageInput;
        }
        return ControlNetSourcePlan.TryParseIndex(normalized, out _)
            ? IcLoraVisualGuideSourceKind.ControlNet
            : IcLoraVisualGuideSourceKind.Unknown;
    }

    private static string NormalizeSource(string source)
    {
        string compact = StringUtils.Compact(source);
        if (compact.Length == 0
            || StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraSourceUpload)))
        {
            return Constants.IcLoraSourceUpload;
        }
        if (StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraSourceStageInput)))
        {
            return Constants.IcLoraSourceStageInput;
        }
        if (ControlNetSourcePlan.TryParseIndex(compact, out int sourceIndex))
        {
            return sourceIndex switch
            {
                1 => Constants.ControlNetSourceTwo,
                2 => Constants.ControlNetSourceThree,
                _ => Constants.ControlNetSourceOne,
            };
        }
        return source?.Trim() ?? "";
    }

    private static IcLoraDriveMediaPlan CompileDriveMedia(UploadedMediaSpec media) => new(
        ResolveDriveMediaKind(media?.Data),
        media?.Data,
        media?.FileName);

    internal static IcLoraDriveMediaKind ResolveDriveMediaKind(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return IcLoraDriveMediaKind.None;
        }
        if (data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return IcLoraDriveMediaKind.Image;
        }
        if (data.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase))
        {
            return IcLoraDriveMediaKind.Video;
        }
        if (data.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase))
        {
            return IcLoraDriveMediaKind.Audio;
        }
        return IcLoraDriveMediaKind.Unknown;
    }

    private static void ValidateAutoModel(
        ClipSpec clip,
        IcLoraSpec entry,
        int index,
        ICollection<VideoPlanDiagnostic> diagnostics)
    {
        if (!StringUtils.Equals(entry.Lora, IcLoraWeights.AutoModelToken))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.Preset))
        {
            diagnostics.Add(Error(
                clip,
                index,
                "ltx2.ic-lora.auto-preset-missing",
                "uses [AUTO] but has no preset"));
        }
        else if (string.IsNullOrWhiteSpace(IcLoraWeights.ModelNameFor(entry.Preset)))
        {
            diagnostics.Add(Error(
                clip,
                index,
                "ltx2.ic-lora.auto-preset-unknown",
                $"uses [AUTO], but preset '{entry.Preset}' has no known weights"));
        }
    }

    private static VideoPlanDiagnostic Error(
        ClipSpec clip,
        int entryIndex,
        string code,
        string detail) =>
        new(
            VideoPlanDiagnosticSeverity.Error,
            code,
            $"Clip {clip.Id} IC-LoRA {entryIndex} {detail}.",
            clip.Id);
}
