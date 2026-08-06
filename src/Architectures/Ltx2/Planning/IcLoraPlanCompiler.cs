using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

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
            List<PlanDiagnostic> entryDiagnostics = [];
            if (entry.Stage >= 0 && !authoredStageIndices.Contains(entry.Stage))
            {
                entryDiagnostics.Add(Warning(
                    clip,
                    index,
                    "ltx2.ic-lora.stage-target-invalid",
                    $"targets authored stage {entry.Stage}, which does not exist"));
            }

            IReadOnlySet<IcLoraDriveMediaKind> acceptedKinds =
                IcLoraDriveMediaKinds.AcceptedFor(entry.DriveData, entry.DriveMediaKinds);
            ValidateDriveMediaKinds(clip, entry, index, entryDiagnostics);
            string normalizedSource = NormalizeDriveSource(entry.DriveSource);
            IcLoraMediaSourceKind authoredSource =
                ResolveSourceKind(normalizedSource);
            IcLoraControlMode controlMode =
                CompileControlMode(entry.ControlType);
            int dimensionDownscaleFactor =
                IcLoraDimensionPolicyResolver.Resolve(
                    entry.Preset,
                    entry.Lora);
            if (entry.DriveData == IcLoraDriveData.None
                && authoredSource != IcLoraMediaSourceKind.Upload)
            {
                entryDiagnostics.Add(Warning(
                    clip,
                    index,
                    "ltx2.ic-lora.drive-source-contradictory",
                    "sets DriveData to None, so DriveSource must use the canonical Upload value"));
            }
            if (HasUploadedMedia(entry.DriveMedia)
                && authoredSource != IcLoraMediaSourceKind.Upload)
            {
                entryDiagnostics.Add(Warning(
                    clip,
                    index,
                    "ltx2.ic-lora.drive-media-source-mismatch",
                    "configures DriveMedia, but only DriveSource Upload may consume it"));
            }
            if (controlMode == IcLoraControlMode.Unknown)
            {
                entryDiagnostics.Add(Warning(
                    clip,
                    index,
                    "ltx2.ic-lora.control-mode-unsupported",
                    $"uses unsupported control mode '{entry.ControlType}'"));
            }
            else if (entry.DriveData != IcLoraDriveData.Visual
                && controlMode != IcLoraControlMode.None)
            {
                entryDiagnostics.Add(Warning(
                    clip,
                    index,
                    "ltx2.ic-lora.drive-control-unsupported",
                    $"consumes {entry.DriveData} data and cannot use visual control preprocessing"));
            }
            List<PlanDiagnostic> stageDiagnostics = [];
            List<(int StageIndex, IcLoraPlan Plan)> pendingPlans = [];
            foreach (StageSpec stage in ApplicableStages(clip, entry))
            {
                int reportedBefore = stageDiagnostics.Count;
                if (ArchitectureStageActivity.IsPassthrough(
                        stage,
                        Ltx2ArchitectureModule.Instance.Descriptor))
                {
                    stageDiagnostics.Add(Warning(
                        clip,
                        index,
                        "ltx2.ic-lora.passthrough-stage",
                        $"targets passthrough stage {stage.ClipStageRawIndex}, where IC-LoRA cannot run"));
                    continue;
                }
                IcLoraDrivePlan drive = CompileDrive(
                    stage,
                    normalizedSource,
                    authoredSource,
                    entry.DriveData,
                    entry.DriveMedia,
                    context);
                ValidateDrive(
                    clip,
                    index,
                    normalizedSource,
                    acceptedKinds,
                    entry.DriveMedia,
                    drive,
                    stageDiagnostics);
                if (stageDiagnostics.Count > reportedBefore)
                {
                    continue;
                }
                pendingPlans.Add((stage.ClipStageRawIndex, CompilePlan(
                    index,
                    entry,
                    stage,
                    controlMode,
                    dimensionDownscaleFactor,
                    drive)));
            }

            diagnostics.AddRange(entryDiagnostics);
            diagnostics.AddRange(stageDiagnostics);
            // Entry errors drop all stages; stage errors drop only the affected stage.
            if (entryDiagnostics.Count > 0 || pendingPlans.Count == 0)
            {
                continue;
            }
            foreach ((int stageIndex, IcLoraPlan plan) in pendingPlans)
            {
                stagePlans[stageIndex].Add(plan);
            }
            if (primaryControlNetSourceIndex is null
                && MediaSource.TryParseControlNetIndex(
                    normalizedSource,
                    out int sourceIndex))
            {
                primaryControlNetSourceIndex = sourceIndex;
            }
        }

        foreach ((int stageIndex, ImmutableArray<IcLoraPlan>.Builder plans) in stagePlans)
        {
            List<int> audioEntries = plans
                .Where(plan => plan.HasAudioReference)
                .Select(plan => plan.EntryIndex)
                .ToList();
            if (audioEntries.Count <= 1)
            {
                continue;
            }

            HashSet<int> surplusEntries = [.. audioEntries.Skip(1)];
            for (int planIndex = plans.Count - 1; planIndex >= 0; planIndex--)
            {
                if (surplusEntries.Contains(plans[planIndex].EntryIndex))
                {
                    plans.RemoveAt(planIndex);
                }
            }
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "ltx2.ic-lora.audio-drive-overlap",
                $"Clip {clip.Id} stage {stageIndex} has overlapping audio-consuming IC-LoRAs "
                    + $"({string.Join(", ", audioEntries)}); only entry {audioEntries[0]} will run.",
                clip.Id));
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

    private static IcLoraDrivePlan CompileDrive(
        StageSpec stage,
        string normalizedSource,
        IcLoraMediaSourceKind source,
        IcLoraDriveData stream,
        UploadedMediaSpec upload,
        ArchitectureClipCompileContext context)
    {
        if (stream == IcLoraDriveData.None)
        {
            return new(
                stream,
                IcLoraMediaSourceKind.Upload,
                IcLoraDriveMediaKind.None,
                null,
                null);
        }

        if (source == IcLoraMediaSourceKind.Upload)
        {
            return new(
                stream,
                source,
                ResolveDriveMediaKind(upload?.Data),
                upload,
                null);
        }
        if (source == IcLoraMediaSourceKind.Incoming)
        {
            IcLoraDriveMediaKind kind = ResolveIncomingKind(stage, context);
            return new(
                stream,
                source,
                kind,
                null,
                null);
        }
        if (source == IcLoraMediaSourceKind.ControlNet
            && MediaSource.TryParseControlNetIndex(
                normalizedSource,
                out int controlNetIndex))
        {
            return new(
                stream,
                source,
                IcLoraDriveMediaKind.Video,
                null,
                controlNetIndex);
        }
        return new(
            stream,
            IcLoraMediaSourceKind.Unknown,
            IcLoraDriveMediaKind.Unknown,
            null,
            null);
    }

    private static IcLoraPlan CompilePlan(
        int entryIndex,
        IcLoraSpec entry,
        StageSpec stage,
        IcLoraControlMode controlMode,
        int dimensionDownscaleFactor,
        IcLoraDrivePlan drive)
    {
        double? guideStrength = null;
        if (drive.Stream == IcLoraDriveData.Visual)
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
            else if (drive.Source != IcLoraMediaSourceKind.ControlNet)
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
            drive,
            dimensionDownscaleFactor,
            guideStrength);
    }

    private static IcLoraDriveMediaKind ResolveIncomingKind(
        StageSpec stage,
        ArchitectureClipCompileContext context)
    {
        if (stage.ClipStageIndex > 0
            || context.HasPreviousClipOutput
            || context.EntryMode == ArchitectureEntryMode.InitVideo)
        {
            return IcLoraDriveMediaKind.Video;
        }
        return context.EntryMode == ArchitectureEntryMode.ImageToVideo
            ? IcLoraDriveMediaKind.Image
            : IcLoraDriveMediaKind.None;
    }

    private static void ValidateDrive(
        ClipSpec clip,
        int entryIndex,
        string rawSource,
        IReadOnlySet<IcLoraDriveMediaKind> acceptedKinds,
        UploadedMediaSpec authoredUpload,
        IcLoraDrivePlan drive,
        ICollection<PlanDiagnostic> diagnostics)
    {
        if (drive.Stream == IcLoraDriveData.None)
        {
            if (HasUploadedMedia(authoredUpload))
            {
                diagnostics.Add(Warning(
                    clip,
                    entryIndex,
                    "ltx2.ic-lora.drive-media-unused",
                    "sets DriveData to None but still contains uploaded Drive Media"));
            }
            return;
        }
        if (drive.Source == IcLoraMediaSourceKind.Unknown)
        {
            diagnostics.Add(Warning(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-source-unsupported",
                $"uses unsupported DriveSource '{rawSource}'"));
            return;
        }
        if (drive.Source == IcLoraMediaSourceKind.Incoming
            && drive.MediaKind == IcLoraDriveMediaKind.None)
        {
            diagnostics.Add(Warning(
                clip,
                entryIndex,
                "ltx2.ic-lora.incoming-unavailable",
                "requests Incoming media where no clip-entry, previous-stage, or previous-clip media exists"));
            return;
        }
        if (drive.Source == IcLoraMediaSourceKind.Upload
            && !HasUploadedMedia(drive.Upload))
        {
            diagnostics.Add(Warning(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-media-missing",
                $"requires uploaded {drive.Stream} Drive Media"));
            return;
        }
        if (drive.Source == IcLoraMediaSourceKind.ControlNet
            && drive.Stream == IcLoraDriveData.Audio)
        {
            diagnostics.Add(Warning(
                clip,
                entryIndex,
                "ltx2.ic-lora.audio-controlnet-unsupported",
                "cannot consume Audio data from a legacy ControlNet drive source"));
            return;
        }
        if (!acceptedKinds.Contains(drive.MediaKind))
        {
            diagnostics.Add(Warning(
                clip,
                entryIndex,
                "ltx2.ic-lora.drive-media-kind-unsupported",
                $"cannot consume {drive.Stream} data from {drive.MediaKind} media; expected "
                    + $"{string.Join(" or ", acceptedKinds)}"));
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
        foreach (string rawKind in entry.DriveMediaKinds)
        {
            if (IcLoraDriveMediaKinds.TryParse(
                    rawKind,
                    out IcLoraDriveMediaKind kind))
            {
                explicitKinds.Add(kind);
            }
        }

        IReadOnlySet<IcLoraDriveMediaKind> generic =
            IcLoraDriveMediaKinds.AcceptedFor(entry.DriveData);
        bool contradictory = explicitKinds.Any(kind => !generic.Contains(kind))
            || (entry.DriveData != IcLoraDriveData.None && explicitKinds.Count == 0);
        if (contradictory)
        {
            diagnostics.Add(Warning(
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
        if (StringUtils.Equals(normalized, MediaSource.Upload))
        {
            return IcLoraMediaSourceKind.Upload;
        }
        if (StringUtils.Equals(normalized, MediaSource.Incoming))
        {
            return IcLoraMediaSourceKind.Incoming;
        }
        return MediaSource.TryParseControlNetIndex(normalized, out _)
            ? IcLoraMediaSourceKind.ControlNet
            : IcLoraMediaSourceKind.Unknown;
    }

    private static string NormalizeDriveSource(string source)
    {
        string compact = StringUtils.Compact(source);
        if (compact.Length == 0
            || StringUtils.Equals(compact, StringUtils.Compact(MediaSource.Upload)))
        {
            return MediaSource.Upload;
        }
        if (StringUtils.Equals(compact, StringUtils.Compact(MediaSource.Incoming))
            || StringUtils.Equals(
                compact,
                StringUtils.Compact(Constants.IcLoraLegacySourceStageInput)))
        {
            return MediaSource.Incoming;
        }
        if (MediaSource.TryParseControlNetIndex(compact, out int sourceIndex))
        {
            return MediaSource.FormatControlNet(sourceIndex);
        }
        return source?.Trim() ?? "";
    }

    private static bool HasUploadedMedia(UploadedMediaSpec media) =>
        !string.IsNullOrWhiteSpace(media?.Data);

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

    private static PlanDiagnostic Warning(
        ClipSpec clip,
        int entryIndex,
        string code,
        string detail) =>
        new(
            PlanDiagnosticSeverity.Warning,
            code,
            $"Clip {clip.Id} IC-LoRA {entryIndex} {detail}.",
            clip.Id);
}
