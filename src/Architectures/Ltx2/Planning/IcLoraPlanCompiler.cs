using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Compiles stage-applicable IC-LoRAs from explicit source and data-stream intent. Presets select
/// weights and genuinely custom graph behavior elsewhere; they do not change media interpretation.
/// </summary>
internal static class IcLoraPlanCompiler
{
    internal static IReadOnlyList<VideoPlanDiagnostic> ValidateClip(ClipSpec clip) =>
        ValidateClip(
            clip,
            new(
                0,
                0,
                0,
                clip.SourceVideo is null
                    ? ArchitectureEntryMode.ImageToVideo
                    : ArchitectureEntryMode.SourceVideo));

    internal static IReadOnlyList<VideoPlanDiagnostic> ValidateClip(
        ClipSpec clip,
        ArchitectureClipCompileContext context)
    {
        List<VideoPlanDiagnostic> diagnostics = [];
        HashSet<int> authoredStageIndices = clip.AuthoredStages is { Count: > 0 }
            ? [.. clip.AuthoredStages.Select(stage => stage.RawIndex)]
            : [.. (clip.Stages ?? []).Select(stage => stage.ClipStageRawIndex)];
        IReadOnlyList<IcLoraSpec> entries = clip.IcLoras ?? [];

        for (int index = 0; index < entries.Count; index++)
        {
            IcLoraSpec entry = entries[index];
            if (!Enum.IsDefined(entry.DriveData))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.drive-data-unsupported",
                    "has malformed DriveData; expected None, Visual, or Audio"));
            }
            if (entry.Stage >= 0 && !authoredStageIndices.Contains(entry.Stage))
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.stage-target-invalid",
                    $"targets authored stage {entry.Stage}, which does not exist"));
            }

            IcLoraDriveMediaContract contract =
                IcLoraDriveMediaContracts.Resolve(entry.DriveData, entry.DriveMediaKinds);
            ValidateDriveMediaKinds(clip, entry, index, diagnostics);
            IcLoraDriveMediaPlan driveMedia = CompileDriveMedia(entry.DriveMedia);
            IcLoraMediaSourceKind authoredSource =
                ResolveSourceKind(NormalizeDriveSource(entry.DriveSource));
            if (contract.DriveData == IcLoraDriveData.None
                && authoredSource != IcLoraMediaSourceKind.Upload)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.drive-source-contradictory",
                    "sets DriveData to None, so DriveSource must use the canonical Upload value"));
            }
            if (driveMedia.IsConfigured && authoredSource != IcLoraMediaSourceKind.Upload)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.drive-media-source-mismatch",
                    "configures DriveMedia, but only DriveSource Upload may consume it"));
            }
            foreach (StageSpec stage in ApplicableStages(clip, entry))
            {
                IcLoraMediaInputPlan input = CompileMediaInput(
                    clip,
                    stage,
                    entry,
                    contract,
                    driveMedia,
                    context);
                ValidateInput(clip, index, contract, driveMedia, input, diagnostics);
            }

            IcLoraControlMode controlMode = CompileControlMode(entry.ControlType);
            if (controlMode == IcLoraControlMode.Unknown)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.control-mode-unsupported",
                    $"uses unsupported control mode '{entry.ControlType}'"));
            }
            else if (!contract.ConsumesVisual && controlMode != IcLoraControlMode.None)
            {
                diagnostics.Add(Error(
                    clip,
                    index,
                    "ltx2.ic-lora.drive-control-unsupported",
                    $"consumes {entry.DriveData} data and cannot use visual control preprocessing"));
            }

            ValidateAutoModel(clip, entry, index, diagnostics);
        }

        foreach (StageSpec stage in clip.Stages ?? [])
        {
            List<int> applicable = [];
            List<int> audioEntries = [];
            for (int index = 0; index < entries.Count; index++)
            {
                IcLoraSpec entry = entries[index];
                if (entry.Stage >= 0 && entry.Stage != stage.ClipStageRawIndex)
                {
                    continue;
                }
                applicable.Add(index);
                if (entry.DriveData == IcLoraDriveData.Audio)
                {
                    audioEntries.Add(index);
                }
            }

            if (stage.IsPassthrough && applicable.Count > 0)
            {
                diagnostics.Add(new(
                    VideoPlanDiagnosticSeverity.Error,
                    "ltx2.ic-lora.passthrough-stage",
                    $"Clip {clip.Id} stage {stage.ClipStageRawIndex} is passthrough, so applicable "
                        + $"IC-LoRAs ({string.Join(", ", applicable)}) cannot run; target a generating stage.",
                    clip.Id));
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
        }
        return diagnostics.AsReadOnly();
    }

    internal static ImmutableArray<IcLoraPlan> Compile(ClipSpec clip, StageSpec stage) =>
        Compile(
            clip,
            stage,
            new(
                0,
                0,
                0,
                clip.SourceVideo is null
                    ? ArchitectureEntryMode.ImageToVideo
                    : ArchitectureEntryMode.SourceVideo));

    internal static ImmutableArray<IcLoraPlan> Compile(
        ClipSpec clip,
        StageSpec stage,
        ArchitectureClipCompileContext context)
    {
        ImmutableArray<IcLoraPlan>.Builder plans = ImmutableArray.CreateBuilder<IcLoraPlan>();
        IReadOnlyList<IcLoraSpec> entries = clip.IcLoras ?? [];
        for (int index = 0; index < entries.Count; index++)
        {
            IcLoraSpec entry = entries[index];
            if (entry.Stage >= 0 && entry.Stage != stage.ClipStageRawIndex)
            {
                continue;
            }

            IcLoraDriveMediaContract contract =
                IcLoraDriveMediaContracts.Resolve(entry.DriveData, entry.DriveMediaKinds);
            IcLoraDriveMediaPlan driveMedia = CompileDriveMedia(entry.DriveMedia);
            IcLoraMediaInputPlan input = CompileMediaInput(
                clip,
                stage,
                entry,
                contract,
                driveMedia,
                context);
            double? guideStrength = null;
            if (contract.ConsumesVisual && input.HasInput)
            {
                if (
                    stage.IcLoraStrengths is { } strengths &&
                    index < strengths.Count)
                {
                    guideStrength = strengths[index];
                }
                else if (stage.ControlNetStrength is double stageStrength)
                {
                    guideStrength = stageStrength;
                }
                else if (input.Source != IcLoraMediaSourceKind.ControlNet)
                {
                    guideStrength = 1.0;
                }
            }

            plans.Add(new(
                index,
                entry.Lora,
                StringUtils.Equals(entry.Lora, IcLoraWeights.AutoModelToken),
                entry.Preset,
                entry.Strength,
                entry.AttentionStrength,
                CompileControlMode(entry.ControlType),
                contract,
                driveMedia,
                input,
                IcLoraDimensionPolicyResolver.Resolve(entry.Preset, entry.Lora),
                guideStrength,
                entry.Hdr));
        }
        return plans.ToImmutable();
    }

    internal static int? ResolvePrimaryControlNetSourceIndex(ClipSpec clip)
    {
        foreach (IcLoraSpec entry in clip.IcLoras ?? [])
        {
            if (ControlNetSourcePlan.TryParseIndex(
                NormalizeDriveSource(entry.DriveSource),
                out int sourceIndex))
            {
                return sourceIndex;
            }
        }
        return null;
    }

    private static IEnumerable<StageSpec> ApplicableStages(ClipSpec clip, IcLoraSpec entry) =>
        (clip.Stages ?? []).Where(stage =>
            entry.Stage < 0 || entry.Stage == stage.ClipStageRawIndex);

    private static IcLoraMediaInputPlan CompileMediaInput(
        ClipSpec clip,
        StageSpec stage,
        IcLoraSpec entry,
        IcLoraDriveMediaContract contract,
        IcLoraDriveMediaPlan driveMedia,
        ArchitectureClipCompileContext context)
    {
        string raw = NormalizeDriveSource(entry.DriveSource);
        if (contract.DriveData == IcLoraDriveData.None)
        {
            return new(
                IcLoraMediaSourceKind.LoaderOnly,
                raw,
                IcLoraDriveMediaKind.None,
                null,
                HasInput: false);
        }

        IcLoraMediaSourceKind source = ResolveSourceKind(raw);
        if (source == IcLoraMediaSourceKind.Upload)
        {
            return new(
                source,
                raw,
                driveMedia.Kind,
                null,
                driveMedia.IsConfigured);
        }
        if (source == IcLoraMediaSourceKind.Incoming)
        {
            IcLoraDriveMediaKind kind = ResolveIncomingKind(clip, stage, context);
            return new(source, raw, kind, null, kind != IcLoraDriveMediaKind.None);
        }
        if (source == IcLoraMediaSourceKind.ControlNet
            && ControlNetSourcePlan.TryParseIndex(raw, out int controlNetIndex))
        {
            return new(
                source,
                raw,
                IcLoraDriveMediaKind.Video,
                controlNetIndex,
                HasInput: true);
        }
        return new(
            IcLoraMediaSourceKind.Unknown,
            raw,
            IcLoraDriveMediaKind.Unknown,
            null,
            HasInput: false);
    }

    private static IcLoraDriveMediaKind ResolveIncomingKind(
        ClipSpec clip,
        StageSpec stage,
        ArchitectureClipCompileContext context)
    {
        if (stage.ClipStageIndex > 0
            || clip.SourceVideo is not null
            || context.HasPreviousClipOutput
            || context.EntryMode is ArchitectureEntryMode.SourceVideo
                or ArchitectureEntryMode.RefineVideo)
        {
            return IcLoraDriveMediaKind.Video;
        }
        return context.EntryMode == ArchitectureEntryMode.ImageToVideo
            ? IcLoraDriveMediaKind.Image
            : IcLoraDriveMediaKind.None;
    }

    private static void ValidateInput(
        ClipSpec clip,
        int entryIndex,
        IcLoraDriveMediaContract contract,
        IcLoraDriveMediaPlan driveMedia,
        IcLoraMediaInputPlan input,
        ICollection<VideoPlanDiagnostic> diagnostics)
    {
        if (contract.DriveData == IcLoraDriveData.None)
        {
            if (driveMedia.IsConfigured)
            {
                diagnostics.Add(Error(
                    clip,
                    entryIndex,
                    "ltx2.ic-lora.drive-media-unused",
                    "sets DriveData to None but still contains uploaded Drive Media"));
            }
            return;
        }
        if (input.Source == IcLoraMediaSourceKind.Unknown)
        {
            diagnostics.Add(Error(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-source-unsupported",
                $"uses unsupported DriveSource '{input.RawSource}'"));
            return;
        }
        if (input.Source == IcLoraMediaSourceKind.Incoming && !input.HasInput)
        {
            diagnostics.Add(Error(
                clip,
                entryIndex,
                "ltx2.ic-lora.incoming-unavailable",
                "requests Incoming media where no clip-entry, previous-stage, or previous-clip media exists"));
            return;
        }
        if (input.Source == IcLoraMediaSourceKind.Upload && !input.HasInput)
        {
            diagnostics.Add(Error(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-media-missing",
                $"requires uploaded {contract.DriveData} Drive Media"));
            return;
        }
        if (input.Source == IcLoraMediaSourceKind.ControlNet && contract.ConsumesAudio)
        {
            diagnostics.Add(Error(
                clip,
                entryIndex,
                "ltx2.ic-lora.audio-controlnet-unsupported",
                "cannot consume Audio data from a legacy ControlNet drive source"));
            return;
        }
        if (input.HasInput && !contract.Accepts(input.Kind))
        {
            diagnostics.Add(Error(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-media-kind-unsupported",
                $"cannot consume {contract.DriveData} data from {input.Kind} media; expected "
                    + $"{string.Join(" or ", contract.AcceptedKinds)}"));
        }
    }

    private static void ValidateDriveMediaKinds(
        ClipSpec clip,
        IcLoraSpec entry,
        int entryIndex,
        ICollection<VideoPlanDiagnostic> diagnostics)
    {
        if (entry.DriveMediaKinds is null)
        {
            return;
        }

        HashSet<IcLoraDriveMediaKind> explicitKinds = [];
        bool malformed = false;
        foreach (string rawKind in entry.DriveMediaKinds)
        {
            if (!IcLoraDriveMediaContracts.TryParseKind(
                    rawKind,
                    out IcLoraDriveMediaKind kind)
                || !explicitKinds.Add(kind))
            {
                malformed = true;
            }
        }
        if (malformed)
        {
            diagnostics.Add(Error(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-media-kinds-malformed",
                "has malformed DriveMediaKinds; expected a unique list containing image, video, or audio"));
        }

        IcLoraDriveMediaContract generic =
            IcLoraDriveMediaContracts.Resolve(entry.DriveData);
        bool contradictory = explicitKinds.Any(kind => !generic.Accepts(kind))
            || (generic.RequiresInput && explicitKinds.Count == 0);
        if (contradictory)
        {
            diagnostics.Add(Error(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-media-kinds-contradictory",
                $"sets DriveData to {entry.DriveData}, but DriveMediaKinds "
                    + $"[{string.Join(", ", entry.DriveMediaKinds)}] cannot supply that stream"));
        }
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

    private static IcLoraMediaSourceKind ResolveSourceKind(string source)
    {
        string normalized = NormalizeDriveSource(source);
        if (StringUtils.Equals(normalized, Constants.IcLoraSourceUpload))
        {
            return IcLoraMediaSourceKind.Upload;
        }
        if (StringUtils.Equals(normalized, Constants.IcLoraSourceIncoming))
        {
            return IcLoraMediaSourceKind.Incoming;
        }
        return ControlNetSourcePlan.TryParseIndex(normalized, out _)
            ? IcLoraMediaSourceKind.ControlNet
            : IcLoraMediaSourceKind.Unknown;
    }

    private static string NormalizeDriveSource(string source)
    {
        string compact = StringUtils.Compact(source);
        if (compact.Length == 0
            || StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraSourceUpload)))
        {
            return Constants.IcLoraSourceUpload;
        }
        if (StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraSourceIncoming))
            || StringUtils.Equals(
                compact,
                StringUtils.Compact(Constants.IcLoraLegacySourceStageInput)))
        {
            return Constants.IcLoraSourceIncoming;
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
