using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Coordinates one compiled stage across focused graph/runtime collaborators.</summary>
internal class StageRunner
{
    private readonly WorkflowGenerator _generator;
    private readonly LtxStageOrchestrator _stageOrchestrator;
    private readonly StageUpscaleGraphBuilder _upscaleGraphBuilder;
    private readonly StageFramePreparer _framePreparer;
    private readonly IcLoraStageInputResolver _icLoraStageInputResolver;
    private readonly StageRuntimeArtifactCapture _artifactCapture;

    public StageRunner(
        WorkflowGenerator generator,
        LtxStageOrchestrator stageOrchestrator)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _stageOrchestrator = stageOrchestrator
            ?? throw new ArgumentNullException(nameof(stageOrchestrator));
        _upscaleGraphBuilder = new StageUpscaleGraphBuilder(generator);
        _framePreparer = new StageFramePreparer(
            generator,
            _upscaleGraphBuilder,
            new PlannedStagePromptResolver(generator));
        _icLoraStageInputResolver = new IcLoraStageInputResolver(generator);
        _artifactCapture = new StageRuntimeArtifactCapture(generator);
    }

    public virtual RuntimeArtifact RunStage(
        StagePlan stage,
        int sectionId,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        ClipContext clipContext,
        StageExecutionOptions executionOptions,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(clipContext);
        ArgumentNullException.ThrowIfNull(rootPolicy);
        if (_generator.CurrentMedia is null)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: stage {stage.StageId} has no input media.");
        }

        ClipPlan clip = clipContext.PlannedClip;
        if (stage.IsPassthrough
            && !rootPolicy.ReplacesTextToVideoRootStage(stage, clip))
        {
            RunPassthroughStage(stage, sectionId, clipContext);
            return _artifactCapture.Capture(stage);
        }

        using ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(
            _generator.UserInput,
            clip.ClipId,
            sectionId);
        using ParamSnapshot loraScope = ApplyStageLoras(_generator.UserInput, stage);

        StageFrame stageFrame = _framePreparer.Prepare(
            stage,
            sectionId,
            clipContext,
            executionOptions,
            rootPolicy);
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.GenInfo;
        using IDisposable controlNetScope = AltImageToVideoScope.Post(genInfo, currentGenInfo =>
        {
            bool needsCrop = new IcLoraApplicator(_generator).ApplyIcLoras(
                currentGenInfo,
                clip,
                stage,
                clip.Frames,
                _icLoraStageInputResolver.Resolve(stageFrame));
            if (needsCrop)
            {
                stageFrame.NeedsCropGuidesAfterSampler = true;
            }
            new IcLoraAudioReferenceApplicator(_generator).ApplyAudioReferenceTokens(
                currentGenInfo,
                clip,
                stageFrame);
        });

        _stageOrchestrator.RunLtxPath(
            guideReference,
            refStore,
            genInfo,
            stageFrame,
            stageFrame.SourceMedia,
            stageFrame.PriorOutputPath,
            stageFrame.PostVideoChain);
        return _artifactCapture.Capture(stage);
    }

    private void RunPassthroughStage(
        StagePlan stage,
        int sectionId,
        ClipContext clipContext)
    {
        LtxAudioReuseState.PrepareReusableAudio(_generator, clipContext, stage);
        LtxPostVideoChainCapture postVideoChain =
            LtxPostVideoChainCapture.TryCapture(_generator, clipContext, stage);
        _ = _upscaleGraphBuilder.Apply(clipContext, stage, sectionId, postVideoChain);
    }

    private static ParamSnapshot ApplyStageLoras(T2IParamInput input, StagePlan stage)
    {
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        if (payload.Loras.IsDefaultOrEmpty)
        {
            return null;
        }

        List<string> loras = [.. input.Get(T2IParamTypes.Loras) ?? []];
        List<string> weights = [.. input.Get(T2IParamTypes.LoraWeights) ?? []];
        List<string> tencWeights = [.. input.Get(T2IParamTypes.LoraTencWeights) ?? []];
        List<string> confinements = [.. input.Get(T2IParamTypes.LoraSectionConfinement) ?? []];
        List<(string, string, string)> rows = [.. payload.Loras.Select(lora => (
            lora.Name,
            LoraParams.FormatWeight(lora.ModelWeight),
            LoraParams.FormatWeight(lora.TextEncoderWeight)))];
        return LoraParams.AppendVideoScoped(
            input,
            loras,
            weights,
            tencWeights,
            confinements,
            rows);
    }
}
