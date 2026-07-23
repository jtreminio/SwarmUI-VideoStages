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
            if (entry.Stage >= 0 && !authoredStageIndices.Contains(entry.Stage))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.stage-target-invalid",
                    $"targets authored stage {entry.Stage}, which does not exist"));
            }

            IcLoraDriveSourceKind sourceKind = ResolveSourceKind(entry.Source);
            if (sourceKind == IcLoraDriveSourceKind.Unknown)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.source-unsupported",
                    $"uses unsupported drive source '{entry.Source}'"));
            }
            if (sourceKind == IcLoraDriveSourceKind.StageInput
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
            if (sourceKind == IcLoraDriveSourceKind.UploadedMedia
                && !string.IsNullOrWhiteSpace(entry.Video?.Data)
                && ResolveUploadedMediaKind(entry.Video.Data) == IcLoraUploadedMediaKind.Unknown)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.upload-kind-unsupported",
                    "contains uploaded drive data that is neither an image nor a video"));
            }
            if (CompileControlMode(entry.ControlType) == IcLoraControlMode.Unknown)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.control-mode-unsupported",
                    $"uses unsupported control mode '{entry.ControlType}'"));
            }
            if (StringUtils.Equals(entry.Lora, IcLoraWeights.AutoModelToken)
                && string.IsNullOrWhiteSpace(entry.Preset))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.auto-preset-missing",
                    "uses [AUTO] but has no preset"));
            }
            else if (StringUtils.Equals(entry.Lora, IcLoraWeights.AutoModelToken)
                && string.IsNullOrWhiteSpace(IcLoraWeights.ModelNameFor(entry.Preset)))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.auto-preset-unknown",
                    $"uses [AUTO], but preset '{entry.Preset}' has no known weights"));
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

            IcLoraDrivePlan drive = CompileDrive(clip, entry);
            double? guideStrength = null;
            if (drive.HasDriveMedia)
            {
                if (stage.ControlNetStrength is double stageStrength)
                {
                    guideStrength = stageStrength;
                }
                else if (drive.Kind != IcLoraDriveSourceKind.ControlNet)
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
                drive,
                guideStrength));
        }
        return plans.ToImmutable();
    }

    private static IcLoraDrivePlan CompileDrive(ClipSpec clip, IcLoraSpec entry)
    {
        string raw = NormalizeSource(entry.Source);
        IcLoraDriveSourceKind kind = ResolveSourceKind(raw);
        if (kind == IcLoraDriveSourceKind.UploadedMedia)
        {
            if (!string.IsNullOrWhiteSpace(entry.Video?.Data))
            {
                string data = entry.Video.Data;
                IcLoraUploadedMediaKind mediaKind = ResolveUploadedMediaKind(data);
                return new(
                    IcLoraDriveSourceKind.UploadedMedia,
                    raw,
                    null,
                    mediaKind,
                    data,
                    HasDriveMedia: true);
            }
            if (clip.SourceVideo is not null)
            {
                return new(
                    IcLoraDriveSourceKind.SourcedClipInput,
                    raw,
                    null,
                    IcLoraUploadedMediaKind.None,
                    null,
                    HasDriveMedia: true);
            }
            return new(
                IcLoraDriveSourceKind.LoaderOnly,
                raw,
                null,
                IcLoraUploadedMediaKind.None,
                null,
                HasDriveMedia: false);
        }
        if (kind == IcLoraDriveSourceKind.StageInput)
        {
            return new(
                IcLoraDriveSourceKind.StageInput,
                raw,
                null,
                IcLoraUploadedMediaKind.None,
                null,
                HasDriveMedia: true);
        }
        if (kind == IcLoraDriveSourceKind.ControlNet
            && ControlNetSourcePlan.TryParseIndex(raw, out int controlNetIndex))
        {
            return new(
                IcLoraDriveSourceKind.ControlNet,
                raw,
                controlNetIndex,
                IcLoraUploadedMediaKind.None,
                null,
                HasDriveMedia: true);
        }
        return new(
            IcLoraDriveSourceKind.Unknown,
            raw,
            null,
            IcLoraUploadedMediaKind.None,
            null,
            HasDriveMedia: false);
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

    private static IcLoraDriveSourceKind ResolveSourceKind(string source)
    {
        string normalized = NormalizeSource(source);
        if (StringUtils.Equals(normalized, Constants.IcLoraSourceUpload))
        {
            return IcLoraDriveSourceKind.UploadedMedia;
        }
        if (StringUtils.Equals(normalized, Constants.IcLoraSourceStageInput))
        {
            return IcLoraDriveSourceKind.StageInput;
        }
        return ControlNetSourcePlan.TryParseIndex(normalized, out _)
            ? IcLoraDriveSourceKind.ControlNet
            : IcLoraDriveSourceKind.Unknown;
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

    private static IcLoraUploadedMediaKind ResolveUploadedMediaKind(string data) =>
        data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            ? IcLoraUploadedMediaKind.Image
            : data.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase)
                ? IcLoraUploadedMediaKind.Video
                : IcLoraUploadedMediaKind.Unknown;

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
