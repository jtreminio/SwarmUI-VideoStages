using SwarmUI.Accounts;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal sealed record ClipArchitectureAssignment(
    int ClipId,
    IVideoArchitectureModule Module,
    VideoArchitectureDescriptor Architecture,
    IReadOnlyDictionary<int, ResolvedVideoModel> StageModels);

internal sealed record ArchitecturePlanningResult(
    IReadOnlyDictionary<int, ClipArchitectureAssignment> Clips,
    IReadOnlyList<PlanDiagnostic> Diagnostics)
{
    internal bool HasErrors => Diagnostics.Any(
        diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
}

internal static class ArchitecturePlanResolver
{
    internal static ArchitecturePlanningResult Resolve(
        VideoStagesSpec spec,
        IVideoArchitectureRegistry registry,
        Session session) =>
        Resolve(spec, registry.ForSession(session));

    internal static ArchitecturePlanningResult Resolve(
        VideoStagesSpec spec,
        IVideoArchitectureRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(registry);
        Dictionary<int, ClipArchitectureAssignment> assignments = [];
        List<PlanDiagnostic> diagnostics = [];

        foreach (ClipSpec clip in spec.Clips ?? [])
        {
            IReadOnlyList<AuthoredStageModelSpec> authoredStages =
                clip.AuthoredStages is { Count: > 0 }
                    ? clip.AuthoredStages.OrderBy(stage => stage.RawIndex).ToArray()
                    : clip.Stages.Select(stage => new AuthoredStageModelSpec(
                        stage.ClipStageRawIndex,
                        stage.Model,
                        ModelProfileId: null,
                        Skipped: false)).ToArray();
            Dictionary<int, ResolvedVideoModel> stageModels =
                ResolveAuthoredStages(clip, authoredStages, registry, diagnostics);

            if (clip.Stages is not { Count: > 0 })
            {
                if (clip.InitVideo is not null)
                {
                    ValidateSourceOnlyIdentity(clip, diagnostics);
                    if (authoredStages.Count > 0
                        && stageModels.TryGetValue(
                        authoredStages[0].RawIndex,
                        out ResolvedVideoModel inactiveInitVideoFirstModel))
                    {
                        ValidateSameArchitecture(
                            clip,
                            authoredStages,
                            stageModels,
                            inactiveInitVideoFirstModel,
                            diagnostics);
                    }
                    assignments.TryAdd(clip.Id, new(
                        clip.Id,
                        null,
                        NoneArchitecture.Descriptor,
                        stageModels));
                }
                else if (authoredStages.Count > 0
                    && stageModels.TryGetValue(
                        authoredStages[0].RawIndex,
                        out ResolvedVideoModel inactiveFirstModel))
                {
                    ValidateSameArchitecture(
                        clip,
                        authoredStages,
                        stageModels,
                        inactiveFirstModel,
                        diagnostics);
                    assignments.TryAdd(clip.Id, new(
                        clip.Id,
                        registry.GetModule(inactiveFirstModel.ArchitectureId),
                        inactiveFirstModel.Architecture,
                        stageModels));
                }
                continue;
            }

            AuthoredStageModelSpec firstStage = authoredStages.FirstOrDefault(stage =>
                stage.RawIndex == clip.Stages[0].ClipStageRawIndex)
                ?? authoredStages.First(stage => !stage.Skipped);
            if (!stageModels.TryGetValue(firstStage.RawIndex, out ResolvedVideoModel firstModel))
            {
                continue;
            }
            ValidateSameArchitecture(
                clip,
                authoredStages,
                stageModels,
                firstModel,
                diagnostics);
            assignments.TryAdd(clip.Id, new(
                clip.Id,
                registry.GetModule(firstModel.ArchitectureId),
                firstModel.Architecture,
                stageModels));
        }
        return new(assignments, diagnostics.AsReadOnly());
    }

    private static Dictionary<int, ResolvedVideoModel> ResolveAuthoredStages(
        ClipSpec clip,
        IReadOnlyList<AuthoredStageModelSpec> authoredStages,
        IVideoArchitectureRegistry registry,
        ICollection<PlanDiagnostic> diagnostics)
    {
        Dictionary<int, ResolvedVideoModel> stageModels = [];
        int firstRawIndex = authoredStages.FirstOrDefault(stage => !stage.Skipped)?.RawIndex
            ?? authoredStages.FirstOrDefault()?.RawIndex
            ?? 0;
        foreach (AuthoredStageModelSpec authored in authoredStages)
        {
            if (!registry.TryResolveModel(authored.Model, out ResolvedVideoModel stageModel))
            {
                bool firstStage = authored.RawIndex == firstRawIndex;
                diagnostics.Add(Diagnostic(
                    firstStage
                        ? PlanDiagnosticSeverity.Error
                        : authored.Skipped
                            ? PlanDiagnosticSeverity.Warning
                            : PlanDiagnosticSeverity.Error,
                    firstStage
                        ? "architecture-stage0-model-unresolved"
                        : "architecture-authored-stage-model-unresolved",
                    $"Clip {clip.Id} authored stage {authored.RawIndex} model '{authored.Model}' "
                        + "does not resolve to a registered video architecture.",
                    clip.Id,
                    stageId: null,
                    rawStageIndex: authored.RawIndex));
                continue;
            }
            stageModels[authored.RawIndex] = stageModel;
        }
        return stageModels;
    }

    private static void ValidateSourceOnlyIdentity(
        ClipSpec clip,
        ICollection<PlanDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(clip.AuthoredArchitectureHint)
            && !string.Equals(
                clip.AuthoredArchitectureHint,
                NoneArchitecture.Id.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Diagnostic(
                PlanDiagnosticSeverity.Warning,
                "architecture-source-only-identity-mismatch",
                $"Clip {clip.Id} has no generation stages and therefore requires architecture "
                    + $"'{NoneArchitecture.Id}', but its authored architecture hint is "
                    + $"'{clip.AuthoredArchitectureHint}'.",
                clip.Id,
                stageId: null));
        }
        if (!string.IsNullOrWhiteSpace(clip.AuthoredModelProfileHint)
            && !string.Equals(
                clip.AuthoredModelProfileHint,
                NoneArchitecture.Id.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Diagnostic(
                PlanDiagnosticSeverity.Warning,
                "architecture-source-only-profile-mismatch",
                $"Clip {clip.Id} has no generation stages and therefore requires model profile "
                    + $"'{NoneArchitecture.Id}', but its authored model profile is "
                    + $"'{clip.AuthoredModelProfileHint}'.",
                clip.Id,
                stageId: null));
        }
    }

    private static void ValidateSameArchitecture(
        ClipSpec clip,
        IReadOnlyList<AuthoredStageModelSpec> authoredStages,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ResolvedVideoModel firstModel,
        ICollection<PlanDiagnostic> diagnostics)
    {
        foreach (AuthoredStageModelSpec authored in authoredStages)
        {
            if (!stageModels.TryGetValue(authored.RawIndex, out ResolvedVideoModel stageModel)
                || stageModel.ArchitectureId == firstModel.ArchitectureId)
            {
                if (stageModel is not null
                    && !string.IsNullOrWhiteSpace(firstModel.CompatibilityClassId)
                    && !string.IsNullOrWhiteSpace(stageModel.CompatibilityClassId)
                    && !string.Equals(
                        stageModel.CompatibilityClassId,
                        firstModel.CompatibilityClassId,
                        StringComparison.Ordinal))
                {
                    diagnostics.Add(Error(
                        "architecture-mixed-authored-stage-compatibility",
                        $"Clip {clip.Id} authored stage {authoredStages[0].RawIndex} establishes "
                            + $"host compatibility class '{firstModel.CompatibilityClassId}', "
                            + $"but authored stage {authored.RawIndex}"
                            + (authored.Skipped ? " (skipped)" : "")
                            + $" resolves to '{stageModel.CompatibilityClassId}'. All authored "
                            + "stages in one clip must use one host compatibility class.",
                        clip.Id,
                        stageId: null,
                        rawStageIndex: authored.RawIndex));
                }
                continue;
            }
            diagnostics.Add(Error(
                "architecture-mixed-authored-stage-clip",
                $"Clip {clip.Id} authored stage {authoredStages[0].RawIndex} establishes "
                    + $"architecture '{firstModel.ArchitectureId}', but authored stage "
                    + $"{authored.RawIndex}"
                    + (authored.Skipped ? " (skipped)" : "")
                    + $" resolves to '{stageModel.ArchitectureId}'. All authored stages in one "
                    + "clip must use one architecture.",
                clip.Id,
                stageId: null,
                rawStageIndex: authored.RawIndex));
        }
    }

    private static PlanDiagnostic Diagnostic(
        PlanDiagnosticSeverity severity,
        string code,
        string message,
        int clipId,
        int? stageId,
        int? rawStageIndex = null) =>
        new(
            severity,
            code,
            message,
            clipId,
            stageId,
            rawStageIndex);

    private static PlanDiagnostic Error(
        string code,
        string message,
        int clipId,
        int? stageId,
        int? rawStageIndex = null) =>
        Diagnostic(
            PlanDiagnosticSeverity.Error,
            code,
            message,
            clipId,
            stageId,
            rawStageIndex);
}
