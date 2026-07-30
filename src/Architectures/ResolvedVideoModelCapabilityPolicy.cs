using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>
/// Resolves model-effective capability facts once model selection is known. Clip-wide optional
/// features use the conservative intersection of every active stage model, while stage-local
/// behavior reads the selected stage model directly.
/// </summary>
internal static class ResolvedVideoModelCapabilityPolicy
{
    internal static ArchitectureCapabilityDescriptor ForClip(
        ClipSpec clip,
        VideoArchitectureDescriptor architecture,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels)
    {
        ArchitectureCapabilityDescriptor effective = architecture.Capabilities;
        foreach (ResolvedVideoModel model in ActiveModels(clip, stageModels))
        {
            effective = new(
                effective.Architecture & model.EffectiveCapabilities.Architecture,
                effective.Clip & model.EffectiveCapabilities.Clip,
                effective.Stage & model.EffectiveCapabilities.Stage);
        }
        return effective;
    }

    internal static IReadOnlyList<AudioSourceKind> AudioSourceKindsForClip(
        ClipSpec clip,
        VideoArchitectureDescriptor architecture,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels)
    {
        HashSet<AudioSourceKind> effective = [.. architecture.AudioSourceKinds];
        foreach (ResolvedVideoModel model in ActiveModels(clip, stageModels))
        {
            effective.IntersectWith(model.EffectiveAudioSourceKinds);
        }
        return Array.AsReadOnly(effective.OrderBy(kind => kind).ToArray());
    }

    internal static StageCapability ForStage(
        StageSpec stage,
        VideoArchitectureDescriptor architecture,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels) =>
        stageModels.TryGetValue(stage.ClipStageRawIndex, out ResolvedVideoModel model)
            && model is not null
            ? model.EffectiveCapabilities.Stage
            : architecture.Capabilities.Stage;

    internal static ArchitectureCapabilityDescriptor ForPlanClip(ClipPlan clip)
    {
        ArchitectureCapabilityDescriptor effective =
            clip.Architecture?.Capabilities
            ?? new(
                ArchitectureCapability.None,
                ClipCapability.None,
                StageCapability.None);
        foreach (ResolvedVideoModel model in clip.Stages
            .Select(stage => stage.ResolvedModel)
            .Where(model => model is not null))
        {
            effective = new(
                effective.Architecture & model.EffectiveCapabilities.Architecture,
                effective.Clip & model.EffectiveCapabilities.Clip,
                effective.Stage & model.EffectiveCapabilities.Stage);
        }
        return effective;
    }

    private static IEnumerable<ResolvedVideoModel> ActiveModels(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels)
    {
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            if (stageModels.TryGetValue(stage.ClipStageRawIndex, out ResolvedVideoModel model)
                && model is not null)
            {
                yield return model;
            }
        }
    }
}
