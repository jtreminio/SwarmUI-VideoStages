using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Owns the timeline loop while focused collaborators execute root setup and individual clips.
/// </summary>
internal sealed class StageSequenceRunner(
    TimelineAssembler timelineAssembler,
    ArchitectureRuntimeSessionFactoryRegistry runtimeFactories)
{
    public void Run(
        VideoExecutionPlan plan,
        AudioRuntimeSources preparedAudioSources,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rootPolicy);
        IReadOnlyList<ClipPlan> plannedClips = plan.Clips;
        bool parallelMultiClip = plannedClips.Count > 1;

        TimelineAssemblySession assembly = timelineAssembler.Begin(plan);
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
                plan,
                clipIndex,
                parallelMultiClip,
                HasPreviousTimelineClip: clipIndex > 0,
                PreviousClip: exposesPrevious ? previousClip : null,
                PreviousClipOutput: exposesPrevious ? previousClipOutput : null,
                preparedAudioSources,
                assembly,
                rootPolicy);
            DecodedClipArtifact output = runtimeDispatcher.Execute(runtimeContext);
            if (parallelMultiClip)
            {
                clipOutputs.Add(output);
                previousClipOutput = output;
            }
            previousClip = plannedClip;
        }

        if (parallelMultiClip)
        {
            assembly.Assemble(clipOutputs);
        }
        else if (plannedClips[0].Stages.Count == 0)
        {
            assembly.FinalizeUnstagedSingleClip();
        }
    }

}
