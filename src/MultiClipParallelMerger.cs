using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages;

internal sealed class MultiClipParallelMerger(WorkflowGenerator g)
{
    internal const string NodeHelperKey = "videostages.parallel-multi-clip";
    private const int BatchImagesNodeMaxInputs = 50;

    public void Apply(
        IReadOnlyList<WGNodeData> clipOutputsInOrder,
        WGNodeData parallelClipSourceMedia = null)
    {
        if (clipOutputsInOrder is null || clipOutputsInOrder.Count < 2)
        {
            return;
        }

        List<WGNodeData> resolvedAudio = [];
        int sumFrames = 0;
        bool allFramesKnown = true;
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            WGNodeData audio = TryGetClipConcatenatableAudio(clip);
            if (audio is not null)
            {
                resolvedAudio.Add(audio);
            }

            if (allFramesKnown)
            {
                if (clip?.Frames is int f)
                {
                    sumFrames += f;
                }
                else
                {
                    allFramesKnown = false;
                }
            }
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);

        List<INodeOutput> videoOutputs = [];
        HashSet<string> terminalKeys = [];
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            INodeOutput output = bridge.ResolvePath(clip?.Path);
            if (output is null)
            {
                continue;
            }
            videoOutputs.Add(output);
            terminalKeys.Add(OutputKey(output));
        }

        if (videoOutputs.Count < 2)
        {
            return;
        }

        List<INodeOutput> audioOutputs = [];
        foreach (WGNodeData audio in resolvedAudio)
        {
            INodeOutput output = bridge.ResolvePath(audio.Path);
            if (output is not null)
            {
                audioOutputs.Add(output);
            }
        }

        INodeOutput mergedVideo = MergeClipVideosWithBatchImagesNode(bridge, videoOutputs);
        if (audioOutputs.Count > 0 && audioOutputs.Count != videoOutputs.Count)
        {
            Logs.Warning(
                $"VideoStages: merged clip audio omitted — only {audioOutputs.Count} of "
                + $"{videoOutputs.Count} clips have concatenatable audio.");
        }
        INodeOutput mergedAudio = audioOutputs.Count == videoOutputs.Count
            ? CascadeAudioConcat(bridge, audioOutputs)
            : null;

        INodeOutput rootVideoOutput = bridge.ResolvePath(parallelClipSourceMedia?.Path);
        if (rootVideoOutput is not null)
        {
            terminalKeys.Add(OutputKey(rootVideoOutput));
        }

        RetargetSwarmSaveAnimationWsForClipTerminals(bridge, terminalKeys, mergedVideo, mergedAudio);
        BridgeSync.SyncLastId(g);

        WGNodeData template = clipOutputsInOrder[0];
        g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(mergedVideo), g, WGNodeData.DT_VIDEO, template.Compat)
        {
            Width = template.Width,
            Height = template.Height,
            Frames = allFramesKnown ? sumFrames : template.Frames,
            FPS = template.FPS
        };
        if (mergedAudio is not null)
        {
            g.CurrentMedia.AttachedAudio = new WGNodeData(
                WorkflowBridge.ToPath(mergedAudio),
                g,
                WGNodeData.DT_AUDIO,
                template.AttachedAudio?.Compat ?? g.CurrentAudioVae?.Compat);
        }
    }

    private static string OutputKey(INodeOutput output) => $"{output.Node.Id}::{output.SlotIndex}";

    private WGNodeData TryGetClipConcatenatableAudio(WGNodeData clip)
    {
        WGNodeData attached = clip?.AttachedAudio;
        if (attached?.Path is not JArray { Count: 2 })
        {
            return null;
        }

        if (attached.DataType == WGNodeData.DT_AUDIO)
        {
            return attached;
        }

        if (attached.DataType == WGNodeData.DT_LATENT_AUDIO && g.CurrentAudioVae is not null)
        {
            WGNodeData decoded = attached.DecodeLatents(g.CurrentAudioVae, true);
            if (decoded?.Path is JArray { Count: 2 } && decoded.DataType == WGNodeData.DT_AUDIO)
            {
                return decoded;
            }
        }

        return null;
    }

    private static INodeOutput MergeClipVideosWithBatchImagesNode(
        WorkflowBridge bridge,
        IReadOnlyList<INodeOutput> outputs)
    {
        if (outputs.Count == 1)
        {
            return outputs[0];
        }

        List<INodeOutput> layer = [.. outputs];
        while (layer.Count > BatchImagesNodeMaxInputs)
        {
            INodeOutput chunk = AddBatchImagesNode(bridge, layer.Take(BatchImagesNodeMaxInputs));
            List<INodeOutput> next = [chunk];
            for (int i = BatchImagesNodeMaxInputs; i < layer.Count; i++)
            {
                next.Add(layer[i]);
            }
            layer = next;
        }

        return AddBatchImagesNode(bridge, layer);
    }

    private static INodeOutput AddBatchImagesNode(WorkflowBridge bridge, IEnumerable<INodeOutput> imageOutputs)
    {
        BatchImagesNodeNode node = bridge.AddNode(new BatchImagesNodeNode());
        int i = 0;
        foreach (INodeOutput imageOutput in imageOutputs)
        {
            node.ExtraInputs[$"images.image{i}"] = WorkflowBridge.ToPath(imageOutput);
            i++;
        }
        bridge.SyncNode(node);

        return node.IMAGE;
    }

    private static INodeOutput CascadeAudioConcat(WorkflowBridge bridge, IReadOnlyList<INodeOutput> audioOutputs)
    {
        INodeOutput acc = audioOutputs[0];
        for (int i = 1; i < audioOutputs.Count; i++)
        {
            AudioConcatNode concat = bridge.AddNode(new AudioConcatNode());
            concat.Audio1.ConnectToUntyped(acc);
            concat.Audio2.ConnectToUntyped(audioOutputs[i]);
            bridge.SyncNode(concat);
            acc = concat.AUDIO;
        }

        return acc;
    }

    private static void RetargetSwarmSaveAnimationWsForClipTerminals(
        WorkflowBridge bridge,
        HashSet<string> terminalKeys,
        INodeOutput images,
        INodeOutput audio)
    {
        if (images is null || terminalKeys.Count == 0)
        {
            return;
        }

        foreach (SwarmSaveAnimationWSNode save in bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>())
        {
            if (save.Images.Connection is not INodeOutput existingImages
                || !terminalKeys.Contains(OutputKey(existingImages)))
            {
                continue;
            }

            save.Images.ConnectToUntyped(images);
            if (!save.Audio.TryConnectToUntyped(audio))
            {
                save.Audio.Clear();
            }
            bridge.SyncNode(save);
        }
    }
}
