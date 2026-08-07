using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Execution;

internal sealed class RootRuntimeSession
{
    private readonly WorkflowGenerator _generator;
    private readonly RootPlan _rootPlan;
    private readonly IReadOnlySet<string> _hostAnimationSaveIds;
    private readonly IReadOnlySet<string> _capturedRootComponentIds;
    private readonly bool _publishAudio;
    private readonly Dictionary<string, string> _displacedRootRemovals = [];

    /// <summary>The one node id the backend reports as the request's result; every other id under
    /// 100, and everything from 50000 up, it reports as an intermediate.</summary>
    private const string CoreFinalOutputNodeId = "9";

    /// <summary>Where core itself puts a base image save when a video follows it.</summary>
    private const string CoreSupersededImageSaveNodeId = "30";

    private RootRuntimeSession(
        WorkflowGenerator generator,
        RootPlan rootPlan,
        IReadOnlySet<string> hostAnimationSaveIds,
        IReadOnlySet<string> capturedRootComponentIds,
        bool publishAudio)
    {
        _generator = generator;
        _rootPlan = rootPlan;
        _hostAnimationSaveIds = hostAnimationSaveIds;
        _capturedRootComponentIds = capturedRootComponentIds;
        _publishAudio = publishAudio;
    }

    public static RootRuntimeSession Capture(
        WorkflowGenerator generator,
        VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(plan);

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        RuntimeArtifact hostRoot = RuntimeArtifact.Capture(
            generator,
            bridge);
        IReadOnlySet<string> hostAnimationSaveIds = CaptureHostAnimationSaveIds(bridge, hostRoot);
        HashSet<string> componentSeeds = [];
        AddArtifactNodeIds(componentSeeds, hostRoot);
        IReadOnlySet<string> rootComponentIds = componentSeeds.Count == 0
            ? new HashSet<string>()
            : WorkflowGraphCleanup.CollectOwnedRootClosure(
                bridge,
                componentSeeds,
                hostAnimationSaveIds);
        return new RootRuntimeSession(
            generator,
            plan.Root,
            hostAnimationSaveIds,
            rootComponentIds,
            plan.Root.IgnoresHostRootOutput || plan.Clips.Count > 1);
    }

    /// <summary>
    /// The root nodes this session owns, and would remove once the timeline displaces them.
    /// A root node outside this set is one some other sink still holds, so it is neither removed
    /// nor available for a stage to take over.
    /// </summary>
    public IReadOnlySet<string> OwnedRootComponentIds => _capturedRootComponentIds;

    /// <summary>
    /// Every host-root node publishing this timeline deleted, by id and by the class it held when
    /// it died — empty when every node the host built is one a stage took over. Counts the
    /// stale-audio prune too, which runs before the cleanup and removes root nodes of its own;
    /// leaving it out would report a clean sweep over an unclean one.
    /// </summary>
    public IReadOnlyDictionary<string, string> DisplacedRootRemovals => _displacedRootRemovals;

    public void PublishTimeline(RuntimeArtifact timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (!timeline.HasMedia)
        {
            throw Invariant.Failure(
                "the completed timeline did not produce a publishable video artifact.");
        }

        if (!Publish(timeline))
        {
            throw Invariant.Failure(
                "the completed timeline could not be connected to the final output.");
        }

        if (_rootPlan.IgnoresHostRootOutput)
        {
            CleanupDisplacedRoot(timeline);
        }
    }

    // Capture only saves already wired to the host root; stage-authored saves are not publication targets.
    private static IReadOnlySet<string> CaptureHostAnimationSaveIds(
        WorkflowBridge bridge,
        RuntimeArtifact hostRoot)
    {
        INodeOutput hostMedia = hostRoot?.Media?.Output;
        return hostMedia is null
            ? new HashSet<string>()
            : [
                .. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()
                    .Where(save => SameOutput(save.Images.Connection, hostMedia))
                    .Select(save => save.Id)
            ];
    }

    private static bool SameOutput(INodeOutput left, INodeOutput right)
    {
        return left is not null
            && right is not null
            && left.Node.Id == right.Node.Id
            && left.SlotIndex == right.SlotIndex;
    }

    private bool Publish(RuntimeArtifact artifact)
    {
        WGNodeData media = artifact.Media?.ToWGNodeData(_generator);
        if (media?.Path is not JArray { Count: 2 } mediaPath)
        {
            return false;
        }

        // Retained single-clip roots keep their existing save-audio wrapper.
        WGNodeData audio = _publishAudio ? ResolvePublishedAudio(media.AttachedAudio) : null;
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        INodeOutput videoOutput = bridge.ResolvePath(mediaPath);
        if (videoOutput is null)
        {
            return false;
        }

        INodeOutput audioOutput = audio?.Path is JArray { Count: 2 } audioPath
            ? bridge.ResolvePath(audioPath)
            : null;
        List<SwarmSaveAnimationWSNode> hostSaves = _hostAnimationSaveIds
            .Select(id => bridge.Graph.GetNode(id))
            .OfType<SwarmSaveAnimationWSNode>()
            .ToList();
        if (hostSaves.Count == 0)
        {
            WGNodeData vae = artifact.Vae?.ToWGNodeData(_generator) ?? _generator.CurrentVae;
            DemoteCoreBaseImageSave(bridge);
            media.SaveOutput(vae, _generator.CurrentAudioVae, CoreFinalOutputNodeId);
            return true;
        }

        Dictionary<string, string> rootClasses = SnapshotRootClasses(bridge);
        HashSet<string> staleAudioNodeIds = [];
        foreach (SwarmSaveAnimationWSNode save in hostSaves)
        {
            save.Images.ConnectToUntyped(videoOutput);
            if (media.GetRawFPS() is int fps && fps > 0)
            {
                save.Fps.Set((double)fps);
            }
            if (_publishAudio && save.Audio.Connection?.Node?.Id is string staleAudioId)
            {
                staleAudioNodeIds.Add(staleAudioId);
            }
            if (_publishAudio && !save.Audio.TryConnectToUntyped(audioOutput))
            {
                save.Audio.Clear();
            }
        }

        HashSet<string> protectedNodeIds = [$"{mediaPath[0]}"];
        if (audio?.Path is JArray { Count: 2 } publishedAudioPath)
        {
            protectedNodeIds.Add($"{publishedAudioPath[0]}");
        }
        foreach (string staleAudioNodeId in staleAudioNodeIds)
        {
            RecordRootRemovals(
                rootClasses,
                WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(
                    bridge,
                    staleAudioNodeId,
                    protectedNodeIds,
                    _generator.NodeHelpers));
        }
        return true;
    }

    /// <summary>
    /// Core chooses between the two ids by whether a <c>videomodel</c> was requested, and a
    /// timeline never sets one — its models live per-stage — so core leaves the base image sitting
    /// on the result id. Publishing there overwrites whatever holds it, so the image has to move
    /// first or it is destroyed rather than demoted.
    /// </summary>
    private void DemoteCoreBaseImageSave(WorkflowBridge bridge)
    {
        if (bridge.Graph.GetNode(CoreFinalOutputNodeId) is not ComfyNode occupant)
        {
            return;
        }
        // Both classes because SaveOutput picks between them on the backend's feature flags.
        if (occupant is not (SwarmSaveImageWSNode or SaveImageNode))
        {
            throw Invariant.Failure(
                $"core's final output node holds '{occupant.ClassTypeName}' rather than an "
                    + "image save.");
        }
        if (bridge.Graph.GetNode(CoreSupersededImageSaveNodeId) is not null)
        {
            throw Invariant.Failure(
                $"node {CoreSupersededImageSaveNodeId} is occupied, so demoting core's base image "
                    + "save would destroy it.");
        }
        VideoGraphHelpers.RemoveNode(_generator, bridge, CoreFinalOutputNodeId);
        bridge.AddNode(occupant, CoreSupersededImageSaveNodeId);
    }

    private WGNodeData ResolvePublishedAudio(WGNodeData attachedAudio)
    {
        if (attachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO
            && _generator.CurrentAudioVae is not null)
        {
            attachedAudio = attachedAudio.DecodeLatents(_generator.CurrentAudioVae, true);
        }
        return attachedAudio?.DataType == WGNodeData.DT_AUDIO ? attachedAudio : null;
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
        RecordRootRemovals(
            SnapshotRootClasses(bridge),
            WorkflowGraphCleanup.RemoveOwnedNodesNotLive(
                bridge,
                _capturedRootComponentIds,
                liveRoots,
                _generator.NodeHelpers));
    }

    /// <summary>
    /// The class each owned root node holds right now. Taken before a removal because the class a
    /// node dies as is not always the class core built it as — a claimed id carries the stage's
    /// node by then.
    /// </summary>
    private Dictionary<string, string> SnapshotRootClasses(WorkflowBridge bridge) =>
        _capturedRootComponentIds
            .Select(id => (Id: id, Node: bridge.Graph.GetNode(id)))
            .Where(entry => entry.Node is not null)
            .ToDictionary(entry => entry.Id, entry => entry.Node.ClassTypeName);

    private void RecordRootRemovals(
        IReadOnlyDictionary<string, string> rootClasses,
        IReadOnlySet<string> removed)
    {
        foreach (string nodeId in removed.Where(rootClasses.ContainsKey))
        {
            _displacedRootRemovals[nodeId] = rootClasses[nodeId];
        }
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
