using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Execution;

/// <summary>
/// The temporary compatibility boundary between typed LTX stage instructions and the existing
/// graph-building <see cref="StageRunner"/>. It is the only place the plan executor republishes a
/// runtime artifact to <see cref="WorkflowGenerator.CurrentMedia"/> before deep LTX code runs.
/// </summary>
internal sealed class StageExecutionAdapter(
    WorkflowGenerator generator,
    StageRunner legacyStageRunner)
{
    public RuntimeArtifact Execute(
        StagePlan plan,
        int sectionId,
        StageExecutionAdapterContext context,
        RuntimeArtifact input)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);

        // StageRunner and the native LTX helpers are intentionally unchanged in this milestone.
        // Publish exactly once at this compatibility boundary, reconstruct the legacy call from
        // typed plan fields, then immediately recover a typed result for the next planned stage.
        input.PublishTo(generator);
        legacyStageRunner.RunStage(
            plan.ToLegacyStageSpec(),
            sectionId,
            context.GuideReference,
            context.ReferenceStore,
            context.ClipContext);

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        return RuntimeArtifact.Capture(generator, bridge, ArtifactOrigin.StageOutput);
    }
}

/// <summary>
/// Runtime-only inputs not appropriate for the immutable stage plan. Parallel execution remains
/// visible here while the legacy NodeHelpers flag remains in use by the deep LTX implementation.
/// </summary>
internal sealed record StageExecutionAdapterContext(
    StageRefStore.StageRef GuideReference,
    StageRefStore ReferenceStore,
    ClipContext ClipContext,
    bool IsParallelMultiClip,
    int TotalClipCount,
    int ClipIndex,
    int ClipStageIndex);
