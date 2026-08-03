using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

internal sealed class VideoStagesCoordinator(
    WorkflowGenerator g,
    MultiClipParallelMerger merger,
    IReadOnlyList<IArchitectureGenerationSessionProvider> runtimeProviders)
{
    internal void RunConfiguredStages(VideoExecutionPlanContext planContext)
    {
        ArgumentNullException.ThrowIfNull(planContext);
        if (planContext.Plan.Clips.Count == 0)
        {
            return;
        }
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(g, planContext);

        RootExecutionPolicy rootPolicy = new(planContext.Plan);
        AudioRuntimeSources preparedAudioSources = new AudioRuntimeSourceResolver(
            g,
            new AudioHandler(g)).Resolve(planContext.Plan);

        g.LastID = Math.Max(g.LastID, Constants.StagedNodeIdReservationFloor);
        VideoExecutionPlan plan = planContext.Plan;
        IReadOnlyList<ClipPlan> plannedClips = plan.Clips;
        TimelineAssemblySession assembly = new(g, merger, plan);
        ArchitectureTimelineSessionContext sessionContext = new(
            plan,
            preparedAudioSources,
            rootPolicy,
            assembly);
        RuntimeArtifact finalArtifact;
        using (ArchitectureRuntimeDispatcher runtimeDispatcher =
            new(runtimeProviders.Select(provider => provider.CreateSession(
                sessionContext with
                {
                    OwnsGeneratedRoot =
                        planContext.RootOwnerArchitectureId == provider.ArchitectureId,
                }))))
        {
            ClipPlan previousClip = null;
            DecodedClipArtifact previousClipOutput = null;
            List<DecodedClipArtifact> clipOutputs = [];
            for (int clipIndex = 0; clipIndex < plannedClips.Count; clipIndex++)
            {
                ClipPlan plannedClip = plannedClips[clipIndex];
                bool exposesPrevious = clipIndex > 0
                    && plan.Boundaries[clipIndex - 1].Effective != BoundaryJoinType.Cut
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

            finalArtifact = plannedClips.Count > 1
                ? assembly.Assemble(clipOutputs)
                : assembly.FinalizeSingleClip(clipOutputs[0]);
        }
        finalArtifact = new TimelineFrameInterpolator(g).Apply(
            finalArtifact,
            plan);
        // Compat metadata is architecture-neutral; the runtime artifact retains VAE ownership.
        if (finalArtifact.Media is not null)
        {
            finalArtifact.Media.Compat = null;
            if (finalArtifact.Media.AttachedAudio is not null)
            {
                finalArtifact.Media.AttachedAudio.Compat = null;
            }
        }
        finalArtifact.PublishTo(g);
        rootSession.PublishTimeline(finalArtifact);
    }
}
