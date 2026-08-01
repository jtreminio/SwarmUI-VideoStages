using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiled IC-LoRA plans, source selection, and diagnostics for one clip.</summary>
internal sealed record IcLoraClipPlanCompilation(
    ImmutableDictionary<int, ImmutableArray<IcLoraPlan>> Stages,
    int? PrimaryControlNetSourceIndex,
    ImmutableArray<PlanDiagnostic> Diagnostics);

internal static class IcLoraPlanCompiler
{
    internal static IcLoraClipPlanCompilation CompileClip(
        ClipSpec clip,
        ArchitectureClipCompileContext context)
    {
        List<PlanDiagnostic> diagnostics = [];
        HashSet<int> authoredStageIndices = clip.AuthoredStages is { Count: > 0 }
            ? [.. clip.AuthoredStages.Select(stage => stage.RawIndex)]
            : [.. (clip.Stages ?? []).Select(stage => stage.ClipStageRawIndex)];
        Dictionary<int, ImmutableArray<IcLoraPlan>.Builder> stagePlans =
            (clip.Stages ?? []).ToDictionary(
                stage => stage.ClipStageRawIndex,
                _ => ImmutableArray.CreateBuilder<IcLoraPlan>());
        IReadOnlyList<IcLoraSpec> entries = clip.IcLoras ?? [];
        int? primaryControlNetSourceIndex = null;

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
            string normalizedSource = NormalizeDriveSource(entry.DriveSource);
            IcLoraMediaSourceKind authoredSource =
                ResolveSourceKind(normalizedSource);
            IcLoraControlMode controlMode =
                CompileControlMode(entry.ControlType);
            int dimensionDownscaleFactor =
                IcLoraDimensionPolicyResolver.Resolve(
                    entry.Preset,
                    entry.Lora);
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
            foreach (StageSpec stage in ApplicableStages(clip, entry))
            {
                IcLoraMediaInputPlan input = CompileMediaInput(
                    clip,
                    stage,
                    normalizedSource,
                    authoredSource,
                    contract,
                    driveMedia,
                    context);
                ValidateInput(clip, index, contract, driveMedia, input, diagnostics);
                stagePlans[stage.ClipStageRawIndex].Add(CompilePlan(
                    index,
                    entry,
                    stage,
                    controlMode,
                    dimensionDownscaleFactor,
                    contract,
                    driveMedia,
                    input));
            }

            ValidateAutoModel(clip, entry, index, diagnostics);
            if (primaryControlNetSourceIndex is null
                && ControlNetSourcePlan.TryParseIndex(
                    normalizedSource,
                    out int sourceIndex))
            {
                primaryControlNetSourceIndex = sourceIndex;
            }
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
                    PlanDiagnosticSeverity.Error,
                    "ltx2.ic-lora.passthrough-stage",
                    $"Clip {clip.Id} stage {stage.ClipStageRawIndex} is passthrough, so applicable "
                        + $"IC-LoRAs ({string.Join(", ", applicable)}) cannot run; target a generating stage.",
                    clip.Id));
            }
            if (audioEntries.Count > 1)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.ic-lora.audio-drive-overlap",
                    $"Clip {clip.Id} stage {stage.ClipStageRawIndex} has overlapping audio-consuming "
                        + $"IC-LoRAs ({string.Join(", ", audioEntries)}); use one speaker drive per stage.",
                    clip.Id));
            }
        }
        return new(
            stagePlans.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value.ToImmutable()),
            primaryControlNetSourceIndex,
            diagnostics.ToImmutableArray());
    }

    private static IEnumerable<StageSpec> ApplicableStages(ClipSpec clip, IcLoraSpec entry) =>
        (clip.Stages ?? []).Where(stage =>
            entry.Stage < 0 || entry.Stage == stage.ClipStageRawIndex);

    private static IcLoraMediaInputPlan CompileMediaInput(
        ClipSpec clip,
        StageSpec stage,
        string normalizedSource,
        IcLoraMediaSourceKind source,
        IcLoraDriveMediaContract contract,
        IcLoraDriveMediaPlan driveMedia,
        ArchitectureClipCompileContext context)
    {
        if (contract.DriveData == IcLoraDriveData.None)
        {
            return new(
                IcLoraMediaSourceKind.LoaderOnly,
                normalizedSource,
                IcLoraDriveMediaKind.None,
                null,
                HasInput: false);
        }

        if (source == IcLoraMediaSourceKind.Upload)
        {
            return new(
                source,
                normalizedSource,
                driveMedia.Kind,
                null,
                driveMedia.IsConfigured);
        }
        if (source == IcLoraMediaSourceKind.Incoming)
        {
            IcLoraDriveMediaKind kind = ResolveIncomingKind(clip, stage, context);
            return new(
                source,
                normalizedSource,
                kind,
                null,
                kind != IcLoraDriveMediaKind.None);
        }
        if (source == IcLoraMediaSourceKind.ControlNet
            && ControlNetSourcePlan.TryParseIndex(
                normalizedSource,
                out int controlNetIndex))
        {
            return new(
                source,
                normalizedSource,
                IcLoraDriveMediaKind.Video,
                controlNetIndex,
                HasInput: true);
        }
        return new(
            IcLoraMediaSourceKind.Unknown,
            normalizedSource,
            IcLoraDriveMediaKind.Unknown,
            null,
            HasInput: false);
    }

    private static IcLoraPlan CompilePlan(
        int entryIndex,
        IcLoraSpec entry,
        StageSpec stage,
        IcLoraControlMode controlMode,
        int dimensionDownscaleFactor,
        IcLoraDriveMediaContract contract,
        IcLoraDriveMediaPlan driveMedia,
        IcLoraMediaInputPlan input)
    {
        double? guideStrength = null;
        if (contract.ConsumesVisual && input.HasInput)
        {
            if (stage.IcLoraStrengths is { } strengths
                && entryIndex < strengths.Count)
            {
                guideStrength = strengths[entryIndex];
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

        return new(
            entryIndex,
            entry.Lora,
            StringUtils.Equals(entry.Lora, IcLoraWeights.AutoModelToken),
            entry.Preset,
            entry.Strength,
            entry.AttentionStrength,
            controlMode,
            contract,
            driveMedia,
            input,
            dimensionDownscaleFactor,
            guideStrength);
    }

    private static IcLoraDriveMediaKind ResolveIncomingKind(
        ClipSpec clip,
        StageSpec stage,
        ArchitectureClipCompileContext context)
    {
        if (stage.ClipStageIndex > 0
            || clip.InitVideo is not null
            || context.HasPreviousClipOutput
            || context.EntryMode == ArchitectureEntryMode.InitVideo)
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
        ICollection<PlanDiagnostic> diagnostics)
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
        ICollection<PlanDiagnostic> diagnostics)
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
        ICollection<PlanDiagnostic> diagnostics)
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

    private static PlanDiagnostic Error(
        ClipSpec clip,
        int entryIndex,
        string code,
        string detail) =>
        new(
            PlanDiagnosticSeverity.Error,
            code,
            $"Clip {clip.Id} IC-LoRA {entryIndex} {detail}.",
            clip.Id);
}
