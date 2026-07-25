using SwarmUI.Text2Image;
using System.Globalization;

namespace VideoStages;

/// <summary>Shared append logic for the four LoRA list params. Both LoRA appliers
/// (clip/stage LoRAs in StageRunner, prompt-scoped LoRAs in PromptParser) pad the
/// parallel lists, append video-section-confined rows, and swap the params behind
/// one ParamSnapshot — this is that one implementation.</summary>
internal static class LoraParams
{
    public static string FormatWeight(double weight) =>
        weight.ToString(CultureInfo.InvariantCulture);

    /// <summary>Pads weights/tencWeights/confinements up to loras.Count, appends
    /// <paramref name="rows"/> confined to the video section, snapshots the four
    /// LoRA params, and writes the new lists. Caller disposes the snapshot.</summary>
    public static ParamSnapshot AppendVideoScoped(
        T2IParamInput input,
        List<string> loras,
        List<string> weights,
        List<string> tencWeights,
        List<string> confinements,
        IReadOnlyList<(string Name, string Weight, string TencWeight)> rows)
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
            confinements.Add($"{T2IParamInput.SectionID_Video}");
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
