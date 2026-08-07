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
            AppendWithOwnWeights(plans, clip.Loras, target);
        }
        else
        {
            AppendWithStageWeights(plans, clip.Loras, stage.LoraWeights);
        }
        AppendWithOwnWeights(plans, stage.Loras, target);
        return plans.ToImmutable();
    }

    private static void AppendWithStageWeights(
        ImmutableArray<LoraPlan>.Builder plans,
        IReadOnlyList<LoraRef> loras,
        IReadOnlyList<double> weights)
    {
        if (loras is null)
        {
            return;
        }
        for (int index = 0; index < loras.Count; index++)
        {
            LoraRef lora = loras[index];
            double weight = index < (weights?.Count ?? 0)
                ? weights[index]
                : lora.Weight;
            if (weight == 0)
            {
                continue;
            }
            plans.Add(new LoraPlan(
                lora.Name,
                weight,
                lora.TencWeight ?? weight));
        }
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
