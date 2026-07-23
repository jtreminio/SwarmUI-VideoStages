using SwarmUI.Text2Image;

namespace VideoStages;

/// <summary>Selects prompt-section-confined LoRAs for one clip stage and appends them to its parameter scope.</summary>
internal static class VideoScopedLoraSelector
{
    public static ParamSnapshot Apply(T2IParamInput input, int clipIndex, int stageSectionId)
    {
        if (!input.TryGet(T2IParamTypes.Loras, out List<string> loras)
            || loras is null
            || loras.Count == 0)
        {
            return null;
        }

        List<string> confinements = input.Get(T2IParamTypes.LoraSectionConfinement) ?? [];
        if (confinements.Count == 0)
        {
            return null;
        }

        List<string> weights = input.Get(T2IParamTypes.LoraWeights) ?? [];
        List<string> textEncoderWeights = input.Get(T2IParamTypes.LoraTencWeights) ?? [];
        int globalCid = Constants.SectionID_VideoClip;
        int clipCid = VideoStagesExtension.SectionIdForClip(clipIndex);
        List<int> selectedIndices = [];

        for (int index = 0; index < loras.Count; index++)
        {
            if (index >= confinements.Count || !int.TryParse(confinements[index], out int confinementId))
            {
                continue;
            }
            if (confinementId == globalCid || confinementId == clipCid || confinementId == stageSectionId)
            {
                selectedIndices.Add(index);
            }
        }

        if (selectedIndices.Count == 0)
        {
            return null;
        }

        List<(string, string, string)> rows = [];
        foreach (int index in selectedIndices)
        {
            string weight = index < weights.Count ? weights[index] : "1";
            string textEncoderWeight = index < textEncoderWeights.Count ? textEncoderWeights[index] : weight;
            rows.Add((loras[index], weight, textEncoderWeight));
        }

        return LoraParams.AppendVideoScoped(
            input,
            [.. loras],
            [.. weights],
            [.. textEncoderWeights],
            [.. confinements],
            rows);
    }
}
