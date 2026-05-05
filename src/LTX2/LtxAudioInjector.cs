using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;

namespace VideoStages.LTX2;

internal sealed class LtxAudioInjector(
    WorkflowGenerator g,
    JsonParser jsonParser,
    RootVideoStageResizer rootVideoStageResizer)
{
    private const int AudioInjectionIdBase = 52300;
    private const int AudioInjectionEnsureFallbackSlot = 50;

    public bool TryInject(AudioStageDetector.Detection detection, bool matchVideoLengthToAudio = true)
    {
        Console.WriteLine($"[Inj] enter detection.Audio={detection?.Audio} isLTXV2={g.IsLTXV2()} audioVae={g.CurrentAudioVae}");
        if (detection?.Audio is null || !g.IsLTXV2() || g.CurrentAudioVae is null)
        {
            Console.WriteLine("[Inj] early-out");
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        (
            List<string> concatIds,
            HashSet<string> removableSourceIds,
            int? workflowFps) = FindConcatsToReplace(bridge);
        Console.WriteLine($"[Inj] concatIds.Count={concatIds.Count}");
        if (concatIds.Count == 0)
        {
            return false;
        }

        WGNodeData adjustedAudio = detection.Audio;
        if (matchVideoLengthToAudio)
        {
            int fps = ResolveFps(workflowFps);
            JToken lengthFramesAudioSource = LtxAudioPathResolution.ResolveLengthToFramesAudioSource(
                g,
                detection.Audio.Path,
                g.GetStableDynamicID(AudioInjectionIdBase + AudioInjectionEnsureFallbackSlot, 0));
            // ResolveLengthToFramesAudioSource may have written to g.Workflow; refresh bridge.
            bridge = WorkflowBridge.Create(g.Workflow);
            SwarmAudioLengthToFramesNode lengthToFrames = CreateLengthToFramesNode(bridge, lengthFramesAudioSource, fps);
            ApplyFramesConnectionToRemovableAudioSources(bridge, removableSourceIds, lengthToFrames.Frames);
            LtxFrameCountConnector.ApplyToExistingSources(g, WorkflowBridge.ToPath(lengthToFrames.Frames));
            adjustedAudio = new(WorkflowBridge.ToPath(lengthToFrames.Audio), g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
        }
        WGNodeData encodedAudio = adjustedAudio.EncodeToLatent(g.CurrentAudioVae);
        // EncodeToLatent and the helpers above mutate g.Workflow; refresh bridge.
        bridge = WorkflowBridge.Create(g.Workflow);
        SetLatentNoiseMaskNode setMask = CreateAudioMaskNode(bridge, encodedAudio.Path);
        ReplaceAudioLatentConnections(bridge, concatIds, setMask);
        RemoveUnusedSourceNodes(bridge, removableSourceIds);
        return true;
    }

    private static (List<string> ConcatIds, HashSet<string> RemovableSourceIds, int? WorkflowFps)
        FindConcatsToReplace(WorkflowBridge bridge)
    {
        List<string> concatIds = [];
        HashSet<string> removableSourceIds = [];
        int? workflowFps = null;
        foreach (LTXVConcatAVLatentNode concat in bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>())
        {
            if (concat.AudioLatent.Connection?.Node is not LTXVEmptyLatentAudioNode emptyAudio)
            {
                continue;
            }

            concatIds.Add(concat.Id);
            removableSourceIds.Add(emptyAudio.Id);
            workflowFps ??= ReadFrameRate(emptyAudio);
        }
        return (concatIds, removableSourceIds, workflowFps);
    }

    private int ResolveFps(int? workflowFps)
    {
        int fps = jsonParser.ResolveFps();
        if (fps <= 0)
        {
            fps = workflowFps ?? g.Text2VideoFPS();
        }
        if (fps <= 0)
        {
            fps = g.UserInput.Get(T2IParamTypes.VideoFPS, 24);
        }
        return fps > 0 ? fps : 24;
    }

    private SwarmAudioLengthToFramesNode CreateLengthToFramesNode(WorkflowBridge bridge, JToken audioPath, int fps)
    {
        SwarmAudioLengthToFramesNode node = new();
        node.FrameRate.Set(fps);
        if (audioPath is JArray pathArray && bridge.ResolvePath(pathArray) is INodeOutput typedAudio)
        {
            node.AudioInput.ConnectToUntyped(typedAudio);
        }
        else if (audioPath is not null)
        {
            node.AudioInput.SetUntyped(audioPath);
        }
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

        SolidMaskNode solidMask = new();
        solidMask.Value.Set(0.0);
        solidMask.Width.Set(width);
        solidMask.Height.Set(height);
        bridge.AddNode(solidMask, g.GetStableDynamicID(AudioInjectionIdBase + 200, 0));

        SetLatentNoiseMaskNode setMask = new();
        if (encodedAudioPath is not null && bridge.ResolvePath(encodedAudioPath) is INodeOutput samples)
        {
            setMask.Samples.ConnectToUntyped(samples);
        }
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

    private static int? ReadFrameRate(LTXVEmptyLatentAudioNode emptyAudio)
    {
        return emptyAudio.FrameRate.LiteralValue switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)Math.Round(d),
            float f => (int)Math.Round(f),
            string s when int.TryParse(s, out int parsed) => parsed,
            string s when double.TryParse(s, out double parsed) => (int)Math.Round(parsed),
            _ => null
        };
    }
}
