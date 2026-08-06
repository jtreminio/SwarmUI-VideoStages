using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>Orders clip and stage LoRAs for one stage.</summary>
internal static class LoraPlanCompiler
{
    internal static ImmutableArray<LoraPlan> Compile(
        ClipSpec clip,
        StageSpec stage,
        LoraTarget target = LoraTarget.ModelAndTextEncoder)
    {
        ImmutableArray<LoraPlan>.Builder plans =
            ImmutableArray.CreateBuilder<LoraPlan>();
        if (stage.LoraWeights is null)
        {
            AppendDirectDefinitions(plans, clip.Loras, target);
        }
        else
        {
            AppendClipDefinitions(plans, clip.Loras, stage.LoraWeights);
        }
        AppendDirectDefinitions(plans, stage.Loras, target);
        return plans.ToImmutable();
    }

    private static void AppendClipDefinitions(
        ImmutableArray<LoraPlan>.Builder plans,
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
            plans.Add(new LoraPlan(
                entry.Name,
                weight,
                entry.TencWeight ?? weight));
        }
    }

    private static void AppendDirectDefinitions(
        ImmutableArray<LoraPlan>.Builder plans,
        IReadOnlyList<LoraRef> entries,
        LoraTarget target)
    {
        foreach (LoraRef entry in entries ?? [])
        {
            double textEncoderWeight = entry.TencWeight ?? entry.Weight;
            bool effective = target == LoraTarget.ModelOnly
                ? entry.Weight != 0
                : entry.Weight != 0 || textEncoderWeight != 0;
            if (!effective)
            {
                continue;
            }
            plans.Add(new LoraPlan(
                entry.Name,
                entry.Weight,
                textEncoderWeight));
        }
    }
}
