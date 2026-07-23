namespace VideoStages.Planning;

using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

/// <summary>Compiles one executable clip and its ordered stage chain.</summary>
internal static class ClipPlanCompiler
{
    internal static ClipPlan Compile(ClipSpec clip, ClipPlanCompilationContext context)
    {
        bool sourced = clip.SourceVideo is not null;
        AudioPlan audio = AudioPlanCompiler.Compile(clip);
        ClipInputKind clipInput = sourced
            ? ClipInputKind.SourceVideo
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
                stage.IsPassthrough,
                RequireStagePayload(context.ArchitecturePayload, stage),
                new StageOutputPlan(
                    IsTimelineTerminal: isClipTerminal && context.IsLastClip && !context.IsMultiClip,
                    context.FirstStageOrdinal + i < context.TotalStageCount - 1
                        ? IntermediateOutputPolicy.ControlledByHostSetting
                        : IntermediateOutputPolicy.NotEligible,
                    clip.SaveAudioTrack))
            {
                ResolvedModel = resolvedModel,
            });
        }

        return new ClipPlan(
            clip.Id,
            clip.Frames,
            clipInput,
            sourced,
            CompileSourceVideo(
                clip.SourceVideo,
                context.Width,
                context.Height,
                context.FramesPerSecond),
            Array.AsReadOnly(stages.ToArray()),
            audio)
        {
            Architecture = context.Architecture?.Architecture,
            ArchitecturePayload = context.ArchitecturePayload,
        };
    }

    private static SourceVideoPlan CompileSourceVideo(
        SourceVideoSpec source,
        int width,
        int height,
        int framesPerSecond) => source is null
            ? null
            : new SourceVideoPlan(
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
            ClipInputKind.SourceVideo => StageInputKind.SourceVideo,
            _ => StageInputKind.RootMedia,
        };
    }

    private static IArchitectureStagePayload RequireStagePayload(
        IArchitectureClipPayload clipPayload,
        StageSpec stage)
    {
        // Invalid plans deliberately remain inspectable with diagnostics. Runtime execution rejects
        // those plans before it can reach a stage with no architecture compilation.
        if (clipPayload is null)
        {
            return null;
        }
        if (clipPayload is not IArchitectureStagePayloadSource source)
        {
            throw new InvalidOperationException(
                $"Clip stage {stage.ClipStageRawIndex} has no architecture stage payload source.");
        }
        IArchitectureStagePayload payload = source.GetStagePayload(stage.ClipStageRawIndex);
        if (payload.ArchitectureId != clipPayload.ArchitectureId)
        {
            throw new InvalidOperationException(
                $"Clip stage {stage.ClipStageRawIndex} payload architecture "
                    + $"'{payload.ArchitectureId}' does not match clip architecture "
                    + $"'{clipPayload.ArchitectureId}'.");
        }
        return payload;
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
    ClipArchitectureAssignment Architecture = null,
    IArchitectureClipPayload ArchitecturePayload = null);
