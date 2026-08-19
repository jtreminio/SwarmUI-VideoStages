using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Planning;

internal static class LoraPlanCompiler
{
    internal static ImmutableArray<LoraPlan> Compile(
        ClipSpec clip,
        LoraTarget target)
    {
        ImmutableArray<LoraPlan>.Builder plans =
            ImmutableArray.CreateBuilder<LoraPlan>();
        AppendWithOwnWeights(plans, clip.Loras, target);
        return plans.ToImmutable();
    }

    private static void AppendWithOwnWeights(
        ImmutableArray<LoraPlan>.Builder plans,
        IReadOnlyList<LoraRef> loras,
        LoraTarget target)
    {
        foreach (LoraRef lora in loras ?? [])
        {
            double textEncoderWeight = lora.TencWeight ?? lora.Weight;
            bool effective = target == LoraTarget.ModelOnly
                ? lora.Weight != 0
                : lora.Weight != 0 || textEncoderWeight != 0;
            if (!effective)
            {
                continue;
            }
            plans.Add(new LoraPlan(
                lora.Name,
                lora.Weight,
                textEncoderWeight));
        }
    }
}
