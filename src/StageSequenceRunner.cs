using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Owns the timeline loop while focused collaborators execute root setup and individual clips.
/// </summary>
internal sealed class StageSequenceRunner(
    WorkflowGenerator generator,
    MultiClipParallelMerger merger,
    ArchitectureRuntimeSessionFactoryRegistry runtimeFactories)
{
    public RuntimeArtifact Run(
        VideoExecutionPlan plan,
        AudioRuntimeSources preparedAudioSources,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rootPolicy);
        IReadOnlyList<ClipPlan> plannedClips = plan.Clips;
        bool parallelMultiClip = plannedClips.Count > 1;

        TimelineAssemblySession assembly = new(generator, merger, plan);
        using ArchitectureRuntimeDispatcher runtimeDispatcher =
            runtimeFactories.CreateDispatcher(new(
                plan,
                preparedAudioSources,
                rootPolicy,
                assembly));

        ClipPlan previousClip = null;
        DecodedClipArtifact previousClipOutput = null;
        List<DecodedClipArtifact> clipOutputs = [];
        for (int clipIndex = 0; clipIndex < plannedClips.Count; clipIndex++)
        {
            ClipPlan plannedClip = plannedClips[clipIndex];
            bool exposesPrevious = clipIndex > 0
                && plan.Boundaries[clipIndex - 1].Effective != BoundaryExecutionMode.Cut
                && previousClip?.Architecture.Id == plannedClip.Architecture.Id;
            ArchitectureClipRuntimeContext runtimeContext = new(
                plannedClip,
                clipIndex,
                PreviousClip: exposesPrevious ? previousClip : null,
                PreviousClipOutput: exposesPrevious ? previousClipOutput : null,
                PreviousTimelineClipOutput: clipIndex > 0 ? previousClipOutput : null);
            DecodedClipArtifact output = runtimeDispatcher.Execute(runtimeContext);
            clipOutputs.Add(output);
            previousClipOutput = output;
            previousClip = plannedClip;
        }

        if (parallelMultiClip)
        {
            return assembly.Assemble(clipOutputs);
        }
        return assembly.FinalizeSingleClip(clipOutputs[0]);
    }
}
