using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
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
            bridge,
            ArtifactOrigin.HostRoot);
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

    public OutputPublication PublishTimeline(RuntimeArtifact timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (_rootPlan.OutputDisposition != TimelineOutputDisposition.PublishTimelineOutput)
        {
            return OutputPublication.NotRequired;
        }
        if (!timeline.HasMedia)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the completed timeline did not produce a publishable video artifact.");
        }

        bool rootIsDisplaced = _rootPlan.Use is RootUse.Discard
            or RootUse.GlobalRefineReplacement;
        OutputPublisher publisher = new(
            _generator,
            _outputs,
            _generator.GetStableDynamicID(OutputPublisher.DefaultFinalSaveId, 0));
        OutputPublication publication = publisher.Publish(
            timeline,
            publishAudio: rootIsDisplaced || _requiresDedicatedAudioPublication);
        if (publication.Result == OutputPublicationResult.Failed)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the completed timeline could not be connected to the final output.");
        }

        if (rootIsDisplaced)
        {
            CleanupDisplacedRoot(timeline);
        }
        return publication;
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

/// <summary>
/// The exact host animation publications that existed when the timeline coordinator took ownership.
/// Saves authored later by individual stages are intentionally not part of this registry.
/// </summary>
internal sealed record OutputRegistry(IReadOnlySet<string> HostAnimationSaveIds)
{
    public static OutputRegistry Capture(
        WorkflowBridge bridge,
        RuntimeArtifact hostRoot)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        INodeOutput hostMedia = hostRoot?.Media?.Output;
        HashSet<string> saveIds = hostMedia is null
            ? []
            : [
                .. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()
                    .Where(save => SameOutput(save.Images.Connection, hostMedia))
                    .Select(save => save.Id)
            ];
        return new OutputRegistry(saveIds);
    }

    private static bool SameOutput(INodeOutput left, INodeOutput right)
    {
        return left is not null
            && right is not null
            && left.Node.Id == right.Node.Id
            && left.SlotIndex == right.SlotIndex;
    }
}

/// <summary>
/// Publishes one final runtime artifact through the captured host output contract. Existing host
/// saves retain their settings and ids; when the host supplied none, one normal animation save is
/// created through the WorkflowGenerator compatibility adapter.
/// </summary>
internal sealed class OutputPublisher(
    WorkflowGenerator generator,
    OutputRegistry outputs,
    string fallbackSaveId)
{
    internal const int DefaultFinalSaveId = 52200;

    public OutputPublication Publish(RuntimeArtifact artifact, bool publishAudio)
    {
        if (generator.UserInput.Get(T2IParamTypes.DoNotSave, false))
        {
            using WorkflowBridge suppressionBridge = WorkflowBridge.Create(generator.Workflow);
            foreach (string saveId in outputs.HostAnimationSaveIds)
            {
                if (suppressionBridge.Graph.GetNode(saveId) is not null)
                {
                    VideoGraphHelpers.RemoveNode(generator, suppressionBridge, saveId);
                }
            }
            return OutputPublication.Suppressed;
        }

        WGNodeData media = artifact.Media?.ToWGNodeData(generator);
        if (media?.Path is not JArray { Count: 2 } mediaPath)
        {
            return OutputPublication.Failed;
        }

        // Parallel multi-clip execution publishes merged audio from a dedicated branch. A retained
        // single-clip I2V root has already spliced its existing save-audio wrapper in place.
        WGNodeData audio = publishAudio ? ResolvePublishedAudio(media.AttachedAudio) : null;
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        INodeOutput videoOutput = bridge.ResolvePath(mediaPath);
        if (videoOutput is null)
        {
            return OutputPublication.Failed;
        }

        INodeOutput audioOutput = audio?.Path is JArray { Count: 2 } audioPath
            ? bridge.ResolvePath(audioPath)
            : null;
        List<SwarmSaveAnimationWSNode> hostSaves = outputs.HostAnimationSaveIds
            .Select(id => bridge.Graph.GetNode(id))
            .OfType<SwarmSaveAnimationWSNode>()
            .ToList();
        if (hostSaves.Count == 0)
        {
            WGNodeData vae = artifact.Vae?.ToWGNodeData(generator) ?? generator.CurrentVae;
            media.SaveOutput(vae, generator.CurrentAudioVae, fallbackSaveId);
            return new(
                OutputPublicationResult.Published,
                new HashSet<string>(StringComparer.Ordinal) { fallbackSaveId });
        }

        HashSet<string> staleAudioNodeIds = [];
        foreach (SwarmSaveAnimationWSNode save in hostSaves)
        {
            save.Images.ConnectToUntyped(videoOutput);
            if (publishAudio && save.Audio.Connection?.Node?.Id is string staleAudioId)
            {
                staleAudioNodeIds.Add(staleAudioId);
            }
            if (publishAudio && !save.Audio.TryConnectToUntyped(audioOutput))
            {
                save.Audio.Clear();
            }
            bridge.SyncNode(save);
        }

        HashSet<string> protectedNodeIds = [$"{mediaPath[0]}"];
        if (audio?.Path is JArray { Count: 2 } publishedAudioPath)
        {
            protectedNodeIds.Add($"{publishedAudioPath[0]}");
        }
        foreach (string staleAudioNodeId in staleAudioNodeIds)
        {
            WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(
                bridge,
                staleAudioNodeId,
                protectedNodeIds,
                generator.NodeHelpers);
        }
        return new(
            OutputPublicationResult.Published,
            new HashSet<string>(hostSaves.Select(save => save.Id), StringComparer.Ordinal));
    }

    private WGNodeData ResolvePublishedAudio(WGNodeData attachedAudio)
    {
        if (attachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO
            && generator.CurrentAudioVae is not null)
        {
            attachedAudio = attachedAudio.DecodeLatents(generator.CurrentAudioVae, true);
        }
        return attachedAudio?.DataType == WGNodeData.DT_AUDIO ? attachedAudio : null;
    }
}

internal enum OutputPublicationResult
{
    NotRequired,
    Published,
    Suppressed,
    Failed,
}

internal sealed record OutputPublication(
    OutputPublicationResult Result,
    IReadOnlySet<string> SaveNodeIds)
{
    public static OutputPublication NotRequired { get; } =
        new(OutputPublicationResult.NotRequired, new HashSet<string>());

    public static OutputPublication Suppressed { get; } =
        new(OutputPublicationResult.Suppressed, new HashSet<string>());

    public static OutputPublication Failed { get; } =
        new(OutputPublicationResult.Failed, new HashSet<string>());
}
