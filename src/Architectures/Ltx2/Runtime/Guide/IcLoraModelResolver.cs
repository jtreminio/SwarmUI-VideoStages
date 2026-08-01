using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed record ResolvedIcLoraModel(IcLoraPlan Plan, T2IModel Model);

/// <summary>Resolves compiled IC-LoRA model identities against installed models.</summary>
internal static class IcLoraModelResolver
{
    internal static List<ResolvedIcLoraModel> Resolve(
        IEnumerable<IcLoraPlan> plans,
        T2IParamInput input)
    {
        List<ResolvedIcLoraModel> resolved = [];
        foreach (IcLoraPlan plan in plans ?? [])
        {
            T2IModel model = Resolve(plan, input);
            if (model is not null)
            {
                resolved.Add(new(plan, model));
            }
        }
        return resolved;
    }

    /// <summary>Returns a preflight error for an invalid [AUTO] selection.</summary>
    internal static string DescribeUnresolvable(IcLoraPlan plan) =>
        plan is not null && plan.UsesAutoModel && !TryResolveAutoModel(plan, out _, out string error)
            ? error
            : null;

    private static T2IModel Resolve(IcLoraPlan plan, T2IParamInput input)
    {
        if (!plan.UsesAutoModel)
        {
            return ResolveLoraModel(plan.ModelName, input);
        }
        return TryResolveAutoModel(plan, out T2IModel model, out string error)
            ? model
            : throw new SwarmUserErrorException(error);
    }

    private static bool TryResolveAutoModel(IcLoraPlan plan, out T2IModel model, out string error)
    {
        model = null;
        if (string.IsNullOrWhiteSpace(plan.Preset)
            || StringUtils.Equals(plan.Preset, "custom"))
        {
            error = "An IC-LoRA is set to [AUTO] but has no preset selected. "
                + "Pick a preset (which names the weights to download) or choose a specific LoRA.";
            return false;
        }
        string autoName = IcLoraWeights.ModelNameFor(plan.Preset);
        if (autoName is null)
        {
            error = $"IC-LoRA [AUTO] preset '{plan.Preset}' has no known weights to download. "
                + "Pick a curated preset or choose a specific LoRA.";
            return false;
        }
        model = FindLoraModel(autoName)
            ?? FindLoraModel(IcLoraWeights.LegacyModelNameFor(plan.Preset));
        if (model is null)
        {
            error = $"IC-LoRA [AUTO] weights '{autoName}' are not installed. The automatic download "
                + "may still be running — wait for it to finish in the timeline editor, or select "
                + "the LoRA manually.";
            return false;
        }
        error = null;
        return true;
    }

    internal static T2IModel ResolveLoraModel(
        string loraName,
        T2IParamInput input)
    {
        T2IModel lora = FindLoraModel(loraName);
        if (lora is null)
        {
            Logs.Error($"LoRA Model '{loraName}' not found in the model set.");
        }
        return lora;
    }

    private static T2IModel FindLoraModel(string loraName)
    {
        if (loraName is null)
        {
            return null;
        }
        if (!Program.T2IModelSets.TryGetValue(
                "LoRA",
                out T2IModelHandler loraHandler))
        {
            Logs.Error("LoRA models are not available.");
            return null;
        }
        return loraHandler.Models.TryGetValue(
                loraName + ".safetensors",
                out T2IModel lora)
            || loraHandler.Models.TryGetValue(loraName, out lora)
            ? lora
            : null;
    }
}
