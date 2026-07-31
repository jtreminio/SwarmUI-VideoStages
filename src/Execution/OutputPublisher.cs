using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Execution;

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
/// saves retain their encoding settings and ids while their media wiring and frame rate follow the
/// completed artifact; when the host supplied none, one normal animation save is created through
/// the WorkflowGenerator compatibility adapter.
/// </summary>
internal sealed class OutputPublisher(
    WorkflowGenerator generator,
    OutputRegistry outputs,
    string fallbackSaveId)
{
    public OutputPublicationResult Publish(RuntimeArtifact artifact, bool publishAudio)
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
            return OutputPublicationResult.Suppressed;
        }

        WGNodeData media = artifact.Media?.ToWGNodeData(generator);
        if (media?.Path is not JArray { Count: 2 } mediaPath)
        {
            return OutputPublicationResult.Failed;
        }

        // Parallel multi-clip execution publishes merged audio from a dedicated branch. A retained
        // single-clip I2V root has already spliced its existing save-audio wrapper in place.
        WGNodeData audio = publishAudio ? ResolvePublishedAudio(media.AttachedAudio) : null;
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
            if (media.GetRawFPS() is int fps && fps > 0)
            {
                save.Fps.Set((double)fps);
            }
            if (publishAudio && save.Audio.Connection?.Node?.Id is string staleAudioId)
            {
                staleAudioNodeIds.Add(staleAudioId);
            }
            if (publishAudio && !save.Audio.TryConnectToUntyped(audioOutput))
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
    NotRequired,
    Published,
    Suppressed,
    Failed,
}

