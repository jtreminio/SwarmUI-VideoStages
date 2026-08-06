using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Execution.Parameters;

/// <summary>Applies one stage's LoRAs and keeps its host model-loader cache in that scope.</summary>
internal sealed class StageModelLoadScope : IDisposable
{
    private readonly WorkflowGenerator _generator;
    private readonly string _loaderKey;
    private readonly ParamSnapshot _promptLoras;
    private readonly ParamSnapshot _plannedLoras;
    private readonly bool _removeLoaderOnDispose;

    internal StageModelLoadScope(
        WorkflowGenerator generator,
        ClipPlan clip,
        StagePlan stage,
        int stageSectionId,
        LoraTarget target =
            LoraTarget.ModelAndTextEncoder,
        int? targetSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);
        _generator = generator;
        _loaderKey = $"modelloader_{stage.ResolvedModel.ModelName}_image2video";
        _promptLoras = VideoScopedLoraSelector.Apply(
            generator.UserInput,
            clip.ClipId,
            stageSectionId,
            target,
            targetSectionId);
        try
        {
            _plannedLoras = LoraParams.ApplyNormalLoras(
                generator.UserInput,
                stage.Core.Loras,
                targetSectionId);
            _removeLoaderOnDispose =
                _promptLoras is not null || _plannedLoras is not null;
            VideoGraphHelpers.RemoveCached(generator, _loaderKey);
        }
        catch
        {
            _plannedLoras?.Dispose();
            _promptLoras?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_removeLoaderOnDispose)
        {
            VideoGraphHelpers.RemoveCached(_generator, _loaderKey);
        }
        _plannedLoras?.Dispose();
        _promptLoras?.Dispose();
    }
}
