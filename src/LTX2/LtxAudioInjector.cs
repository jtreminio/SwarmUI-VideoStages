using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages.LTX2;

internal sealed class LtxAudioInjector(
    WorkflowGenerator g,
    JsonParser jsonParser,
    RootVideoStageResizer rootVideoStageResizer)
{
    private const int AudioInjectionIdBase = 52300;
    private const int AudioInjectionEnsureFallbackSlot = 50;

    public bool TryInject(WGNodeData audio, bool matchVideoLengthToAudio = true)
    {
        if (audio is null || !g.IsLTXV2() || g.CurrentAudioVae is null)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        (List<string> concatIds, HashSet<string> removableSourceIds) = FindConcatsToReplace(bridge);
        if (concatIds.Count == 0)
        {
            return false;
        }

        WGNodeData adjustedAudio = audio;
        if (matchVideoLengthToAudio)
        {
            int fps = jsonParser.ResolveFps();
            JToken lengthFramesAudioSource = LtxAudioPathResolution.ResolveLengthToFramesAudioSource(
                bridge,
                audio.Path,
                g.GetStableDynamicID(AudioInjectionIdBase + AudioInjectionEnsureFallbackSlot, 0));
            SwarmAudioLengthToFramesNode lengthToFrames = CreateLengthToFramesNode(
                bridge,
                lengthFramesAudioSource,
                fps);
            ApplyFramesConnectionToRemovableAudioSources(bridge, removableSourceIds, lengthToFrames.Frames);
            BridgeSync.SyncLastId(g);
            LtxFrameCountConnector.ApplyToExistingSources(g, WorkflowBridge.ToPath(lengthToFrames.Frames));
            adjustedAudio = new(
                WorkflowBridge.ToPath(lengthToFrames.Audio),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae.Compat);
        }

        WGNodeData encodedAudio = adjustedAudio.EncodeToLatent(g.CurrentAudioVae);
        bridge = WorkflowBridge.Create(g.Workflow);
        SetLatentNoiseMaskNode setMask = CreateAudioMaskNode(bridge, encodedAudio.Path);
        ReplaceAudioLatentConnections(bridge, concatIds, setMask);
        RemoveUnusedSourceNodes(bridge, removableSourceIds);
        return true;
    }

    private static (List<string> ConcatIds, HashSet<string> RemovableSourceIds)
        FindConcatsToReplace(WorkflowBridge bridge)
    {
        List<string> concatIds = [];
        HashSet<string> removableSourceIds = [];
        foreach (LTXVConcatAVLatentNode concat in bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>())
        {
            if (concat.AudioLatent.Connection?.Node is not LTXVEmptyLatentAudioNode emptyAudio)
            {
                continue;
            }
            concatIds.Add(concat.Id);
            removableSourceIds.Add(emptyAudio.Id);
        }
        return (concatIds, removableSourceIds);
    }

    private SwarmAudioLengthToFramesNode CreateLengthToFramesNode(WorkflowBridge bridge, JToken audioPath, int fps)
    {
        SwarmAudioLengthToFramesNode node = new SwarmAudioLengthToFramesNode().With(
            FrameRate: fps);
        node.AudioInput.TryConnectToUntyped(bridge.ResolvePath(audioPath as JArray));
        bridge.AddNode(node, g.GetStableDynamicID(AudioInjectionIdBase + 100, 0));
        return node;
    }

    private static void ApplyFramesConnectionToRemovableAudioSources(
        WorkflowBridge bridge,
        HashSet<string> sourceIds,
        NodeOutput<IntType> framesOutput)
    {
        foreach (string sourceId in sourceIds)
        {
            LTXVEmptyLatentAudioNode emptyAudio = bridge.Graph.GetNode<LTXVEmptyLatentAudioNode>(sourceId);
            if (emptyAudio is null)
            {
                continue;
            }
            emptyAudio.FramesNumber.ConnectTo(framesOutput);
            bridge.SyncNode(emptyAudio);
        }
    }

    private SetLatentNoiseMaskNode CreateAudioMaskNode(WorkflowBridge bridge, JArray encodedAudioPath)
    {
        int width = g.UserInput.GetImageWidth();
        int height = g.UserInput.GetImageHeight();
        if (rootVideoStageResizer.TryGetConfiguredRootStageResolution(out int rootWidth, out int rootHeight))
        {
            width = rootWidth;
            height = rootHeight;
        }

        SolidMaskNode solidMask = new SolidMaskNode().With(
            Value: 0.0,
            Width: width,
            Height: height);
        bridge.AddNode(solidMask, g.GetStableDynamicID(AudioInjectionIdBase + 200, 0));

        SetLatentNoiseMaskNode setMask = new();
        setMask.Samples.TryConnectToUntyped(bridge.ResolvePath(encodedAudioPath));
        setMask.Mask.ConnectTo(solidMask.MASK);
        bridge.AddNode(setMask, g.GetStableDynamicID(AudioInjectionIdBase + 300, 0));
        return setMask;
    }

    private static void ReplaceAudioLatentConnections(
        WorkflowBridge bridge,
        List<string> concatIds,
        SetLatentNoiseMaskNode setMask)
    {
        foreach (string concatId in concatIds)
        {
            LTXVConcatAVLatentNode concat = bridge.Graph.GetNode<LTXVConcatAVLatentNode>(concatId);
            if (concat is null)
            {
                continue;
            }
            concat.AudioLatent.ConnectTo(setMask.LATENT);
            bridge.SyncNode(concat);
        }
    }

    private void RemoveUnusedSourceNodes(WorkflowBridge bridge, HashSet<string> removableSourceIds)
    {
        g.UsedInputs = null;
        foreach (string sourceId in removableSourceIds)
        {
            if (!g.NodeIsConnectedAnywhere(sourceId))
            {
                bridge.RemoveNode(sourceId);
            }
        }
    }

}
