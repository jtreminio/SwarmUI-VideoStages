using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>Clip LoRAs before stage LoRAs; a stage's LoraWeights override clip weights by position.</summary>
internal static class LoraPlanCompiler
{
    internal static ImmutableArray<LoraPlan> Compile(
        ClipSpec clip,
        StageSpec stage,
        LoraTarget target)
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
        IReadOnlyList<LoraRef> source = loras ?? [];
        for (int index = 0; index < source.Count; index++)
        {
            LoraRef lora = source[index];
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
