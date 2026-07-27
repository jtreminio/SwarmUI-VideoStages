using System.Collections.Immutable;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Orders clip and stage LoRAs for one stage.</summary>
internal static class NormalLoraPlanCompiler
{
    internal static ImmutableArray<NormalLoraPlan> Compile(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<NormalLoraPlan>.Builder plans = ImmutableArray.CreateBuilder<NormalLoraPlan>();
        if (stage.LoraWeights is null)
        {
            Append(plans, clip.Loras);
        }
        else
        {
            AppendClipDefinitions(plans, clip.Loras, stage.LoraWeights);
        }
        Append(plans, stage.Loras);
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

    private static void Append(
        ImmutableArray<NormalLoraPlan>.Builder plans,
        IReadOnlyList<LoraRef> entries)
    {
        foreach (LoraRef entry in entries ?? [])
        {
            plans.Add(new NormalLoraPlan(
                entry.Name,
                entry.Weight,
                entry.TencWeight ?? entry.Weight));
        }
    }
}
