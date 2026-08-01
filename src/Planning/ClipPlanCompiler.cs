namespace VideoStages.Planning;

using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

/// <summary>Compiles one executable clip and its ordered stage chain.</summary>
internal static class ClipPlanCompiler
{
    internal static ClipPlan Compile(ClipSpec clip, ClipPlanCompilationContext context)
    {
        bool initVideoClip = clip.InitVideo is not null;
        AudioPlan audio = AudioPlanCompiler.Compile(clip);
        ArchitectureClipCompilation architectureCompilation =
            ValidateArchitectureCompilation(clip, context);
        ClipInputKind clipInput = initVideoClip
            ? ClipInputKind.InitVideo
            : context.IsTextToVideo ? ClipInputKind.EmptyLatent : ClipInputKind.RootMedia;
        List<StagePlan> stages = [];
        for (int i = 0; i < (clip.Stages?.Count ?? 0); i++)
        {
            StageSpec stage = clip.Stages[i];
            bool isClipTerminal = i == clip.Stages.Count - 1;
            ResolvedVideoModel resolvedModel = null;
            context.Architecture?.StageModels.TryGetValue(stage.ClipStageRawIndex, out resolvedModel);
            stages.Add(new StagePlan(
                stage.Id,
                stage.ClipStageIndex,
                stage.ClipStageRawIndex,
                ResolveStageInput(clipInput, i),
                ArchitectureStageActivity.IsPassthrough(
                    stage,
                    context.Architecture?.Architecture),
                architectureCompilation?.StagePayloads[stage.ClipStageRawIndex],
                new StageOutputPlan(
                    IsTimelineTerminal: isClipTerminal && context.IsLastClip && !context.IsMultiClip,
                    context.FirstStageOrdinal + i < context.TotalStageCount - 1
                        ? IntermediateOutputEligibility.ControlledByHostSetting
                        : IntermediateOutputEligibility.NotEligible,
                    clip.SaveAudioTrack))
            {
                ResolvedModel = resolvedModel,
            });
        }

        return new ClipPlan(
            clip.Id,
            clip.Frames,
            clipInput,
            initVideoClip,
            CompileInitVideo(
                clip.InitVideo,
                context.Width,
                context.Height,
                context.FramesPerSecond),
            Array.AsReadOnly(stages.ToArray()),
            audio)
        {
            Architecture = context.Architecture?.Architecture,
            ArchitecturePayload = architectureCompilation?.Payload,
            EntryMode = context.EntryMode,
        };
    }

    private static InitVideoPlan CompileInitVideo(
        InitVideoSpec source,
        int width,
        int height,
        int framesPerSecond) => source is null
            ? null
            : new InitVideoPlan(
                source.Data,
                source.FileName,
                source.StartSeconds,
                width,
                height,
                framesPerSecond);

    private static StageInputKind ResolveStageInput(ClipInputKind clipInput, int stageIndex)
    {
        if (stageIndex > 0)
        {
            return StageInputKind.PreviousStage;
        }
        return clipInput switch
        {
            ClipInputKind.EmptyLatent => StageInputKind.EmptyLatent,
            ClipInputKind.InitVideo => StageInputKind.InitVideo,
            _ => StageInputKind.RootMedia,
        };
    }

    private static ArchitectureClipCompilation ValidateArchitectureCompilation(
        ClipSpec clip,
        ClipPlanCompilationContext context)
    {
        // Invalid plans deliberately remain inspectable with diagnostics. Runtime execution rejects
        // those plans before it can reach a stage with no architecture compilation.
        ArchitectureClipCompilation compilation = context.ArchitectureCompilation;
        if (compilation is null)
        {
            return null;
        }

        ArchitectureId clipArchitectureId = compilation.Payload.ArchitectureId;
        ArchitectureId? assignedArchitectureId = context.Architecture?.Architecture.Id;
        if (assignedArchitectureId.HasValue
            && clipArchitectureId != assignedArchitectureId.Value)
        {
            throw new InvalidOperationException(
                $"Clip payload architecture '{clipArchitectureId}' does not match assigned "
                    + $"architecture '{assignedArchitectureId.Value}'.");
        }

        HashSet<int> authoredRawStageIndices = [
            .. (clip.Stages ?? []).Select(stage => stage.ClipStageRawIndex),
        ];
        foreach (int rawStageIndex in authoredRawStageIndices)
        {
            if (!compilation.StagePayloads.ContainsKey(rawStageIndex))
            {
                throw new InvalidOperationException(
                    $"Clip stage {rawStageIndex} has no architecture stage payload.");
            }
        }
        return compilation;
    }
}

internal sealed record ClipPlanCompilationContext(
    bool IsTextToVideo,
    int Width,
    int Height,
    int FramesPerSecond,
    bool IsLastClip,
    bool IsMultiClip,
    int TotalStageCount,
    int FirstStageOrdinal,
    ArchitectureEntryMode EntryMode,
    ClipArchitectureAssignment Architecture = null,
    ArchitectureClipCompilation ArchitectureCompilation = null);
