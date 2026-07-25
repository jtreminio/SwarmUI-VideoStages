using System.Collections.Immutable;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Orders clip and stage LoRAs for one stage.</summary>
internal static class NormalLoraPlanCompiler
{
    internal static ImmutableArray<NormalLoraPlan> Compile(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<NormalLoraPlan>.Builder plans = ImmutableArray.CreateBuilder<NormalLoraPlan>();
        Append(plans, clip.Loras);
        Append(plans, stage.Loras);
        return plans.ToImmutable();
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
