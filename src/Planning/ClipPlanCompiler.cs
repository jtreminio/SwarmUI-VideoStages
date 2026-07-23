namespace VideoStages.Planning;

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
        PromptRelayPlan promptRelay = PromptRelayPlanCompiler.Compile(
            clip,
            context.FramesPerSecond);
        List<StagePlan> stages = [];
        for (int i = 0; i < (clip.Stages?.Count ?? 0); i++)
        {
            StageSpec stage = clip.Stages[i];
            bool isClipTerminal = i == clip.Stages.Count - 1;
            stages.Add(new StagePlan(
                stage.Id,
                stage.ClipStageIndex,
                stage.ClipStageRawIndex,
                ResolveStageInput(clipInput, i),
                stage.IsPassthrough,
                new StageCorePlan(
                    stage.Model,
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    stage.ControlNetStrength,
                    stage.ImageRefWasExplicit),
                GuideReferencePlanCompiler.Compile(stage.ImageReference),
                CompileUpscale(stage),
                NormalLoraPlanCompiler.Compile(clip, stage),
                IcLoraPlanCompiler.Compile(clip, stage),
                CompileRetake(stage.RetakeWindow),
                promptRelay,
                ImageReferencePlanCompiler.Compile(clip, stage),
                CompileAudioAction(audio, stage),
                new StageOutputPlan(
                    IsTimelineTerminal: isClipTerminal && context.IsLastClip && !context.IsMultiClip,
                    context.FirstStageOrdinal + i < context.TotalStageCount - 1
                        ? IntermediateOutputPolicy.ControlledByHostSetting
                        : IntermediateOutputPolicy.NotEligible,
                    clip.SaveAudioTrack)));
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
            audio);
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

    private static StageUpscalePlan CompileUpscale(StageSpec stage)
    {
        StageUpscaleMode mode;
        if (stage.Upscale == 1)
        {
            mode = StageUpscaleMode.None;
        }
        else if (stage.IsPixelUpscale)
        {
            mode = StageUpscaleMode.Pixel;
        }
        else if (stage.IsModelUpscale)
        {
            mode = StageUpscaleMode.Model;
        }
        else if (stage.IsLatentUpscale)
        {
            mode = StageUpscaleMode.Latent;
        }
        else if (stage.IsLatentModelUpscale)
        {
            mode = StageUpscaleMode.LatentModel;
        }
        else
        {
            mode = StageUpscaleMode.Unsupported;
        }

        string raw = stage.UpscaleMethod?.Trim() ?? "";
        int separator = raw.IndexOf('-');
        string methodName = separator >= 0 && separator < raw.Length - 1
            ? raw[(separator + 1)..]
            : raw;
        return new StageUpscalePlan(mode, stage.Upscale, raw, methodName);
    }

    private static RetakePlan CompileRetake(RetakeWindowSpec retake) => retake is null
        ? null
        : new RetakePlan(
            retake.StartFrame,
            retake.LengthFrames,
            retake.Strength);

    private static StageAudioAction CompileAudioAction(AudioPlan audio, StageSpec stage)
    {
        if (!audio.Reuse.IsEligible)
        {
            return StageAudioAction.None;
        }
        if (stage.ClipStageIndex == audio.Reuse.CaptureStageIndex)
        {
            return StageAudioAction.CaptureForReuse;
        }
        return stage.ClipStageIndex >= audio.Reuse.ReuseFromStageIndex
            ? StageAudioAction.ReuseCaptured
            : StageAudioAction.None;
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
    int FirstStageOrdinal);
