using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed record ResolvedIcLoraModel(IcLoraPlan Plan, T2IModel Model);

/// <summary>Centralized model validation for compiled IC-LoRA model identities.</summary>
internal static class IcLoraModelResolver
{
    internal static List<ResolvedIcLoraModel> Resolve(IEnumerable<IcLoraPlan> plans)
    {
        List<ResolvedIcLoraModel> resolved = [];
        foreach (IcLoraPlan plan in plans ?? [])
        {
            T2IModel model = Resolve(plan);
            if (model is not null)
            {
                resolved.Add(new(plan, model));
            }
        }
        return resolved;
    }

    private static T2IModel Resolve(IcLoraPlan plan)
    {
        if (!plan.UsesAutoModel)
        {
            return ResolveLoraModel(plan.ModelName);
        }
        if (string.IsNullOrWhiteSpace(plan.Preset)
            || StringUtils.Equals(plan.Preset, "custom"))
        {
            throw new SwarmUserErrorException(
                "An IC-LoRA is set to [AUTO] but has no preset selected. "
                + "Pick a preset (which names the weights to download) or choose a specific LoRA.");
        }
        string autoName = IcLoraWeights.ModelNameFor(plan.Preset)
            ?? throw new SwarmUserErrorException(
                $"IC-LoRA [AUTO] preset '{plan.Preset}' has no known weights to download. "
                + "Pick a curated preset or choose a specific LoRA.");
        return ResolveLoraModel(autoName)
            ?? throw new SwarmUserErrorException(
                $"IC-LoRA [AUTO] weights '{autoName}' are not installed. The automatic download "
                + "may still be running — wait for it to finish in the timeline editor, or select "
                + "the LoRA manually.");
    }

    internal static T2IModel ResolveLoraModel(string loraName)
    {
        if (!Program.T2IModelSets.TryGetValue(
                "LoRA",
                out T2IModelHandler loraHandler))
        {
            Logs.Error("LoRA models are not available.");
            return null;
        }
        if (!loraHandler.Models.TryGetValue(
                loraName + ".safetensors",
                out T2IModel lora)
            && !loraHandler.Models.TryGetValue(loraName, out lora))
        {
            Logs.Error($"LoRA Model '{loraName}' not found in the model set.");
            return null;
        }
        return lora;
    }
}
