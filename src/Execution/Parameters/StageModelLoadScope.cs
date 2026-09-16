using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Execution.Parameters;

internal sealed class StageModelLoadScope : IDisposable
{
    private readonly WorkflowGenerator _generator;
    private readonly string _loaderKey;
    private readonly ParamSnapshot _promptLoras;
    private readonly ParamSnapshot _plannedLoras;
    private readonly ParamSnapshot _attention;
    private readonly bool _removeLoaderOnDispose;
    private readonly HashSet<string> _existingNodes;

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
        bool stageAttention = clip.Architecture.Features.HasFlag(ArchitectureFeature.StageAttention);
        _existingNodes = stageAttention && stage.Core.UseSparseAttention is not null and not "None"
            ? generator.Workflow.Properties().Select(property => property.Name).ToHashSet()
            : null;
        _removeLoaderOnDispose = _promptLoras is not null
            || stage.Core.Loras.Length > 0
            || (stageAttention && (stage.Core.ModelAttentionBackend is not null
                || stage.Core.UseSparseAttention is not null
                || generator.UserInput.TryGet(ComfyUIBackendExtension.ModelAttentionBackend, out _)
                || generator.UserInput.TryGet(ComfyUIBackendExtension.UseSparseAttention, out _)));
        _attention = stageAttention ? ParamSnapshot.Of(generator.UserInput,
            ComfyUIBackendExtension.ModelAttentionBackend.Type,
            ComfyUIBackendExtension.UseSparseAttention.Type) : null;
        try
        {
            if (stageAttention)
            {
                ApplyAttention(ComfyUIBackendExtension.ModelAttentionBackend, stage.Core.ModelAttentionBackend);
                ApplyAttention(ComfyUIBackendExtension.UseSparseAttention, stage.Core.UseSparseAttention);
            }
            _plannedLoras = LoraParams.ApplyNormalLoras(
                generator.UserInput,
                stage.Core.Loras,
                targetSectionId);
            VideoGraphHelpers.RemoveCached(generator, _loaderKey);
        }
        catch
        {
            _attention?.Dispose();
            _plannedLoras?.Dispose();
            _promptLoras?.Dispose();
            throw;
        }
    }

    private void ApplyAttention(T2IRegisteredParam<string> parameter, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _generator.UserInput.Remove(parameter);
        }
        else
        {
            _generator.UserInput.Set(parameter, value);
        }
    }

    public void Dispose()
    {
        if (_existingNodes is not null)
        {
            foreach (JProperty node in _generator.Workflow.Properties())
            {
                if (!_existingNodes.Contains(node.Name)
                    && node.Value["class_type"]?.Value<string>() == "BlockSparseAttention"
                    && node.Value["inputs"] is JObject inputs)
                {
                    if (inputs["selection"]?.Value<string>() is "vsa" or "sla")
                    {
                        inputs["selection.keep_percent"] = 10.0;
                    }
                    inputs["start_percent"] = 0.2;
                    inputs["end_percent"] = 1.0;
                    inputs["dense_blocks"] = "";
                    inputs["min_tokens"] = 12288;
                    inputs["extra_tokens"] = 256;
                    inputs["sink_conditioning"] = "exact_kv_and_rows";
                    inputs["verbose"] = false;
                }
            }
        }
        if (_removeLoaderOnDispose)
        {
            VideoGraphHelpers.RemoveCached(_generator, _loaderKey);
        }
        _attention?.Dispose();
        _plannedLoras?.Dispose();
        _promptLoras?.Dispose();
    }
}
