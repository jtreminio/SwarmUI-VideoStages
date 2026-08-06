using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>Orders clip and stage LoRAs for one stage.</summary>
internal static class NormalLoraPlanCompiler
{
    internal static ImmutableArray<NormalLoraPlan> Compile(
        ClipSpec clip,
        StageSpec stage,
        NormalLoraTargetPolicy targetPolicy =
            NormalLoraTargetPolicy.ModelAndTextEncoder)
    {
        ImmutableArray<NormalLoraPlan>.Builder plans =
            ImmutableArray.CreateBuilder<NormalLoraPlan>();
        if (stage.LoraWeights is null)
        {
            AppendDirectDefinitions(plans, clip.Loras, targetPolicy);
        }
        else
        {
            AppendClipDefinitions(plans, clip.Loras, stage.LoraWeights);
        }
        AppendDirectDefinitions(plans, stage.Loras, targetPolicy);
        return plans.ToImmutable();
    }

    private static void AppendClipDefinitions(
        ImmutableArray<NormalLoraPlan>.Builder plans,
        IReadOnlyList<LoraRef> entries,
        IReadOnlyList<double> weights)
    {
        if (entries is null)
        {
            return;
        }
        for (int index = 0; index < entries.Count; index++)
        {
            LoraRef entry = entries[index];
            double weight = index < (weights?.Count ?? 0)
                ? weights[index]
                : entry.Weight;
            if (weight == 0)
            {
                continue;
            }
            plans.Add(new NormalLoraPlan(
                entry.Name,
                weight,
                entry.TencWeight ?? weight));
        }
    }

    private static void AppendDirectDefinitions(
        ImmutableArray<NormalLoraPlan>.Builder plans,
        IReadOnlyList<LoraRef> entries,
        NormalLoraTargetPolicy targetPolicy)
    {
        foreach (LoraRef entry in entries ?? [])
        {
            double textEncoderWeight = entry.TencWeight ?? entry.Weight;
            bool effective = targetPolicy == NormalLoraTargetPolicy.ModelOnly
                ? entry.Weight != 0
                : entry.Weight != 0 || textEncoderWeight != 0;
            if (!effective)
            {
                continue;
            }
            plans.Add(new NormalLoraPlan(
                entry.Name,
                entry.Weight,
                textEncoderWeight));
        }
    }
}
