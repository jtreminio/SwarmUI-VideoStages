using SwarmUI.Text2Image;
using System.Globalization;
using VideoStages.Planning;

namespace VideoStages.Execution.Parameters;

/// <summary>Shared append logic for the four LoRA list params. Persisted clip/stage LoRAs and
/// prompt-scoped LoRAs pad the parallel lists, append video-section-confined rows, and swap the
/// params behind one <see cref="ParamSnapshot"/> here.</summary>
internal static class LoraParams
{
    public static string FormatWeight(double weight) =>
        weight.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Applies a compiled normal-LoRA plan to the video section for the lifetime of the returned
    /// snapshot. A null return means the plan was empty and there is nothing to restore.
    /// </summary>
    public static ParamSnapshot ApplyNormalLoras(
        T2IParamInput input,
        IReadOnlyList<LoraPlan> plans,
        int? targetSectionId = null)
    {
        if (plans is null || plans.Count == 0)
        {
            return null;
        }

        List<string> loras = [.. input.Get(T2IParamTypes.Loras) ?? []];
        List<string> weights = [.. input.Get(T2IParamTypes.LoraWeights) ?? []];
        List<string> tencWeights = [.. input.Get(T2IParamTypes.LoraTencWeights) ?? []];
        List<string> confinements = [.. input.Get(T2IParamTypes.LoraSectionConfinement) ?? []];
        List<(string, string, string)> rows = [.. plans.Select(lora => (
            lora.Name,
            FormatWeight(lora.ModelWeight),
            FormatWeight(lora.TextEncoderWeight)))];
        return AppendVideoScoped(
            input,
            loras,
            weights,
            tencWeights,
            confinements,
            rows,
            targetSectionId ?? T2IParamInput.SectionID_Video);
    }

    /// <summary>Pads weights/tencWeights/confinements up to loras.Count, appends
    /// <paramref name="rows"/> confined to the video section, snapshots the four
    /// LoRA params, and writes the new lists. Caller disposes the snapshot.</summary>
    public static ParamSnapshot AppendVideoScoped(
        T2IParamInput input,
        List<string> loras,
        List<string> weights,
        List<string> tencWeights,
        List<string> confinements,
        IReadOnlyList<(string Name, string Weight, string TencWeight)> rows,
        int targetSectionId)
    {
        while (weights.Count < loras.Count)
        {
            weights.Add("1");
        }
        while (tencWeights.Count < loras.Count)
        {
            tencWeights.Add(weights[tencWeights.Count]);
        }
        while (confinements.Count < loras.Count)
        {
            confinements.Add("-1");
        }

        foreach ((string name, string weight, string tencWeight) in rows)
        {
            loras.Add(name);
            weights.Add(weight);
            tencWeights.Add(tencWeight);
            confinements.Add($"{targetSectionId}");
        }

        ParamSnapshot snapshot = ParamSnapshot.Of(input,
            T2IParamTypes.Loras.Type,
            T2IParamTypes.LoraWeights.Type,
            T2IParamTypes.LoraTencWeights.Type,
            T2IParamTypes.LoraSectionConfinement.Type);
        input.Set(T2IParamTypes.Loras, loras);
        input.Set(T2IParamTypes.LoraWeights, weights);
        input.Set(T2IParamTypes.LoraTencWeights, tencWeights);
        input.Set(T2IParamTypes.LoraSectionConfinement, confinements);
        return snapshot;
    }
}
