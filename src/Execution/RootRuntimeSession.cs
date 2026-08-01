using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages.Execution;

/// <summary>
/// Owns the host-root artifact for one video-timeline execution. The session is captured before any
/// coordinator-level media replacement, then publishes the completed timeline before removing a
/// root component that the immutable plan says is displaced.
/// </summary>
internal sealed class RootRuntimeSession
{
    private readonly WorkflowGenerator _generator;
    private readonly RootPlan _rootPlan;
    private readonly OutputRegistry _outputs;
    private readonly IReadOnlySet<string> _capturedRootComponentIds;
    private readonly bool _requiresDedicatedAudioPublication;

    private RootRuntimeSession(
        WorkflowGenerator generator,
        RootPlan rootPlan,
        OutputRegistry outputs,
        IReadOnlySet<string> capturedRootComponentIds,
        bool requiresDedicatedAudioPublication)
    {
        _generator = generator;
        _rootPlan = rootPlan;
        _outputs = outputs;
        _capturedRootComponentIds = capturedRootComponentIds;
        _requiresDedicatedAudioPublication = requiresDedicatedAudioPublication;
    }

    public static RootRuntimeSession Capture(
        WorkflowGenerator generator,
        VideoExecutionPlanContext planContext)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(planContext);

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        RuntimeArtifact hostRoot = RuntimeArtifact.Capture(
            generator,
            bridge);
        OutputRegistry outputs = OutputRegistry.Capture(bridge, hostRoot);
        HashSet<string> componentSeeds = [];
        AddArtifactNodeIds(componentSeeds, hostRoot);
        IReadOnlySet<string> rootComponentIds = componentSeeds.Count == 0
            ? new HashSet<string>()
            : WorkflowGraphCleanup.CollectOwnedRootClosure(
                bridge,
                componentSeeds,
                outputs.HostAnimationSaveIds);
        return new RootRuntimeSession(
            generator,
            planContext.Plan.Root,
            outputs,
            rootComponentIds,
            planContext.Plan.Clips.Count > 1);
    }

    public void PublishTimeline(RuntimeArtifact timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (!timeline.HasMedia)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the completed timeline did not produce a publishable video artifact.");
        }

        bool rootIsDisplaced = _rootPlan.Use is RootUse.Discard;
        OutputPublisher publisher = new(
            _generator,
            _outputs,
            StableNodeIds.Id(_generator, StableNodeIds.FinalSave));
        if (!publisher.Publish(
            timeline,
            publishAudio: rootIsDisplaced || _requiresDedicatedAudioPublication))
        {
            throw new SwarmUserErrorException(
                "VideoStages: the completed timeline could not be connected to the final output.");
        }

        if (rootIsDisplaced)
        {
            CleanupDisplacedRoot(timeline);
        }
    }

    private void CleanupDisplacedRoot(RuntimeArtifact timeline)
    {
        if (_capturedRootComponentIds.Count == 0)
        {
            return;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        HashSet<string> liveRoots = [
            .. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().Select(save => save.Id)
        ];
        AddArtifactNodeIds(liveRoots, timeline);
        WorkflowGraphCleanup.RemoveOwnedNodesNotLive(
            bridge,
            _capturedRootComponentIds,
            liveRoots,
            _generator.NodeHelpers);
    }

    private static void AddArtifactNodeIds(ISet<string> ids, RuntimeArtifact artifact)
    {
        if (artifact?.Media?.Output?.Node?.Id is string mediaId)
        {
            ids.Add(mediaId);
        }
        if (artifact?.Media?.AttachedAudio?.Output?.Node?.Id is string audioId)
        {
            ids.Add(audioId);
        }
    }
}
