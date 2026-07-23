using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Owns the timeline loop while focused collaborators execute root setup and individual clips.
/// </summary>
internal sealed class StageSequenceRunner(
    WorkflowGenerator g,
    TimelineAssembler timelineAssembler,
    StageSequenceRootSetup rootSetup,
    StageGuideReferenceState guideReferences,
    StageClipExecutor clipExecutor)
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

        guideReferences.Reset();
        StageSequenceRootSources rootSources = rootSetup.Prepare(
            preparedAudioSources,
            rootPolicy);
        using StageHostExecutionScope hostScope = new(g, plan, parallelMultiClip);
        TimelineAssemblySession assembly = timelineAssembler.Begin(plan);

        ClipPlan previousClip = null;
        WGNodeData previousClipOutput = null;
        List<RuntimeArtifact> clipOutputs = [];
        for (int clipIndex = 0; clipIndex < plannedClips.Count; clipIndex++)
        {
            ClipPlan plannedClip = plannedClips[clipIndex];
            RuntimeArtifact output = clipExecutor.Execute(new(
                plannedClip,
                plan,
                clipIndex,
                parallelMultiClip,
                previousClip,
                previousClipOutput,
                rootSources,
                assembly,
                hostScope,
                rootPolicy));
            if (parallelMultiClip)
            {
                clipOutputs.Add(output);
                previousClipOutput = output.Media.ToWGNodeData(g);
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
