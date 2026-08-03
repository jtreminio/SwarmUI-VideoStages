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
        Dictionary<ArchitectureId, IVideoGenerationSession> sessions = [];
        bool sessionConstructionCompleted = false;
        try
        {
            foreach (IArchitectureGenerationSessionProvider provider in runtimeProviders)
            {
                IVideoGenerationSession session = provider.CreateSession(
                sessionContext with
                {
                    OwnsGeneratedRoot =
                        planContext.RootOwnerArchitectureId == provider.ArchitectureId,
                });
                ArgumentNullException.ThrowIfNull(session);
                if (!sessions.TryAdd(session.ArchitectureId, session))
                {
                    TryDispose(session);
                    throw VideoStagesInvariant.Failure(
                        $"Duplicate runtime session for architecture "
                            + $"'{session.ArchitectureId}'.");
                }
            }
            sessionConstructionCompleted = true;
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
                ArchitectureId architectureId = plannedClip.Architecture?.Id
                    ?? throw VideoStagesInvariant.Failure(
                        $"Clip {plannedClip.ClipId} has no architecture identity.");
                if (!sessions.TryGetValue(
                        architectureId,
                        out IVideoGenerationSession session))
                {
                    throw VideoStagesInvariant.Failure(
                        $"No runtime session is registered for architecture "
                            + $"'{architectureId}'.");
                }
                DecodedClipArtifact output = session.Execute(runtimeContext)
                    ?? throw VideoStagesInvariant.Failure(
                        $"Architecture '{session.ArchitectureId}' returned no decoded clip "
                            + "artifact.");
                ValidateOutput(output, session, runtimeContext);
                clipOutputs.Add(output);
                previousClipOutput = output;
                previousClip = plannedClip;
            }

            finalArtifact = plannedClips.Count > 1
                ? assembly.Assemble(clipOutputs)
                : assembly.FinalizeSingleClip(clipOutputs[0]);
        }
        finally
        {
            foreach (IVideoGenerationSession session in sessions.Values)
            {
                if (sessionConstructionCompleted)
                {
                    session.Dispose();
                }
                else
                {
                    TryDispose(session);
                }
            }
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

    private static void ValidateOutput(
        DecodedClipArtifact output,
        IVideoGenerationSession session,
        ArchitectureClipRuntimeContext context)
    {
        if (output.ClipId != context.Clip.ClipId)
        {
            throw VideoStagesInvariant.Failure(
                $"Architecture '{session.ArchitectureId}' returned artifact for clip "
                    + $"'{output.ClipId}' instead of planned clip '{context.Clip.ClipId}'.");
        }
        ArchitectureId plannedArchitectureId = context.Clip.Architecture.Id;
        if (output.ArchitectureId != session.ArchitectureId)
        {
            throw VideoStagesInvariant.Failure(
                $"Architecture '{session.ArchitectureId}' returned artifact for architecture "
                    + $"'{output.ArchitectureId}' instead of planned architecture "
                    + $"'{plannedArchitectureId}' for clip '{context.Clip.ClipId}'.");
        }
        output.ValidateDecoded();
    }

    private static void TryDispose(IVideoGenerationSession session)
    {
        try
        {
            session.Dispose();
        }
        catch
        {
            // Preserve the session-construction failure.
        }
    }
}
