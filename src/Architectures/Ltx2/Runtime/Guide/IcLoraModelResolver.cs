using SwarmUI.Core;
using SwarmUI.Text2Image;
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

    private static T2IModel Resolve(IcLoraPlan plan, T2IParamInput input)
    {
        if (!plan.UsesAutoModel)
        {
            return ResolveLoraModel(plan.ModelName, input);
        }
        if (TryResolveAutoModel(plan, out T2IModel model, out string error))
        {
            return model;
        }
        RequestWarnings.Track(
            input,
            $"VideoStages: {error} Ignoring this IC-LoRA.");
        return null;
    }

    private static bool TryResolveAutoModel(IcLoraPlan plan, out T2IModel model, out string error)
    {
        model = null;
        string autoName = IcLoraWeights.ModelNameFor(plan.Preset);
        model = FindLoraModel(autoName)
            ?? FindLoraModel(IcLoraWeights.LegacyModelNameFor(plan.Preset));
        if (model is null)
        {
            string requestedWeights = autoName ?? plan.Preset ?? "[unspecified preset]";
            error = $"IC-LoRA [AUTO] weights '{requestedWeights}' are not installed. The automatic download "
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
            RequestWarnings.Track(
                input,
                $"VideoStages: LoRA Model '{loraName}' was not found in the model set. "
                    + "Ignoring this IC-LoRA.");
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
