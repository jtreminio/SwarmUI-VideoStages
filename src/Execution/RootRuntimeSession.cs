using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using System.Runtime.CompilerServices;
using VideoStages.Planning;

namespace VideoStages.Execution;

/// <summary>
/// Owns the host-root artifact for one LTX execution. The session is captured before any
/// coordinator-level media replacement, then publishes the completed timeline before removing a
/// root component that the immutable plan says is displaced.
/// </summary>
internal sealed class RootRuntimeSession
{
    private readonly WorkflowGenerator _generator;
    private readonly IReadOnlySet<string> _capturedRootComponentIds;

    private RootRuntimeSession(
        WorkflowGenerator generator,
        RootPlan rootPlan,
        RuntimeArtifact hostRoot,
        OutputRegistry outputs,
        IReadOnlySet<string> capturedRootComponentIds)
    {
        _generator = generator;
        RootPlan = rootPlan;
        HostRoot = hostRoot;
        Outputs = outputs;
        _capturedRootComponentIds = capturedRootComponentIds;
    }

    public RootPlan RootPlan { get; }

    public RuntimeArtifact HostRoot { get; }

    public OutputRegistry Outputs { get; }

    public static RootRuntimeSession Capture(
        WorkflowGenerator generator,
        LtxVideoExecutionPlanContext planContext)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(planContext);

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        RuntimeArtifact hostRoot = RuntimeArtifact.Capture(
            generator,
            bridge,
            ArtifactOrigin.HostRoot);
        OutputRegistry outputs = OutputRegistry.Capture(generator, bridge, hostRoot);
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
            hostRoot,
            outputs,
            rootComponentIds);
    }

    public void PublishTimeline(RuntimeArtifact timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (RootPlan.OutputDisposition != TimelineOutputDisposition.PublishTimelineOutput
            || !timeline.HasMedia)
        {
            return;
        }

        bool rootIsDisplaced = RootPlan.Use is RootUse.Discard
            or RootUse.GlobalRefineReplacement;
        OutputPublisher publisher = new(
            _generator,
            Outputs,
            _generator.GetStableDynamicID(OutputPublisher.DefaultFinalSaveId, 0));
        OutputPublicationResult publication = publisher.Publish(
            timeline,
            replaceCapturedAudio: rootIsDisplaced);
        if (publication == OutputPublicationResult.Failed)
        {
            return;
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

/// <summary>
/// The exact host animation publications that existed when the LTX coordinator took ownership.
/// Saves authored later by individual stages are intentionally not part of this registry.
/// </summary>
internal sealed record OutputRegistry(IReadOnlySet<string> HostAnimationSaveIds)
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, OutputRegistry> Registries = new();

    public static OutputRegistry Capture(
        WorkflowGenerator generator,
        WorkflowBridge bridge,
        RuntimeArtifact hostRoot)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(bridge);
        INodeOutput hostMedia = hostRoot?.Media?.Output;
        HashSet<string> saveIds = hostMedia is null
            ? []
            : [
                .. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()
                    .Where(save => SameOutput(save.Images.Connection, hostMedia))
                    .Select(save => save.Id)
            ];
        OutputRegistry registry = new(saveIds);
        Registries.Remove(generator);
        Registries.Add(generator, registry);
        return registry;
    }

    /// <summary>
    /// Once a root session exists, only publications captured as belonging to that root are
    /// allowed to follow the final-host timeline. Stage-authored intermediate saves deliberately
    /// remain attached to the artifact they published. Outside a root session the legacy
    /// retarget behavior is preserved.
    /// </summary>
    public static bool CanAdvanceFinalHostSave(WorkflowGenerator generator, string saveNodeId)
    {
        return generator is null
            || !Registries.TryGetValue(generator, out OutputRegistry registry)
            || registry.HostAnimationSaveIds.Contains(saveNodeId);
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

    public OutputPublicationResult Publish(RuntimeArtifact artifact, bool replaceCapturedAudio)
    {
        if (generator.UserInput.Get(T2IParamTypes.DoNotSave, false))
        {
            using WorkflowBridge suppressionBridge = WorkflowBridge.Create(generator.Workflow);
            foreach (string saveId in outputs.HostAnimationSaveIds)
            {
                if (suppressionBridge.Graph.GetNode(saveId) is not null)
                {
                    suppressionBridge.RemoveNode(saveId);
                }
            }
            return OutputPublicationResult.Suppressed;
        }

        WGNodeData media = artifact.Media?.ToWGNodeData(generator);
        if (media?.Path is not JArray { Count: 2 } mediaPath)
        {
            return OutputPublicationResult.Failed;
        }

        // A retained I2V root keeps the legacy save-audio wrapper exactly as authored. Its stage
        // path already handles any needed retarget. Only a displaced root transfers audio
        // ownership here; eagerly decoding final audio would orphan the host's wrapper chain.
        WGNodeData audio = replaceCapturedAudio
            ? ResolvePublishedAudio(media.AttachedAudio)
            : null;
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        INodeOutput videoOutput = bridge.ResolvePath(mediaPath);
        if (videoOutput is null)
        {
            return OutputPublicationResult.Failed;
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
            return OutputPublicationResult.Published;
        }

        HashSet<string> staleAudioNodeIds = [];
        foreach (SwarmSaveAnimationWSNode save in hostSaves)
        {
            save.Images.ConnectToUntyped(videoOutput);
            if (replaceCapturedAudio)
            {
                if (save.Audio.Connection?.Node?.Id is string staleAudioId)
                {
                    staleAudioNodeIds.Add(staleAudioId);
                }
                if (!save.Audio.TryConnectToUntyped(audioOutput))
                {
                    save.Audio.Clear();
                }
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
        return OutputPublicationResult.Published;
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
    Published,
    Suppressed,
    Failed,
}
