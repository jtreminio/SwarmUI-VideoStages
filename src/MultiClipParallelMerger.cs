using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal sealed class MultiClipParallelMerger(WorkflowGenerator g)
{
    internal const string NodeHelperKey = "videostages.parallel-multi-clip";
    private const int BatchImagesNodeMaxInputs = 50;

    public void Apply(
        IReadOnlyList<WGNodeData> clipOutputsInOrder,
        JArray? parallelRootVideoPath = null)
    {
        if (clipOutputsInOrder is null || clipOutputsInOrder.Count < 2)
        {
            return;
        }

        List<JArray> videoPaths = [];
        List<JArray> audioPaths = [];
        HashSet<string> terminalKeys = [];
        int sumFrames = 0;
        bool allFramesKnown = true;
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            if (clip?.Path is JArray { Count: 2 } vp)
            {
                videoPaths.Add(new JArray(vp[0], vp[1]));
                terminalKeys.Add(OutputKey(vp));
                JArray concatAudioPath = TryGetClipConcatenatableAudioPath(clip);
                if (concatAudioPath is not null)
                {
                    audioPaths.Add(concatAudioPath);
                }
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

        if (videoPaths.Count < 2)
        {
            return;
        }

        JArray mergedVideo = MergeClipVideosWithBatchImagesNode(videoPaths);
        JArray mergedAudio = audioPaths.Count == videoPaths.Count
            ? CascadeAudioConcat(audioPaths)
            : null;

        if (parallelRootVideoPath is { Count: 2 })
        {
            terminalKeys.Add(OutputKey(parallelRootVideoPath));
        }

        RetargetSwarmSaveAnimationWsForClipTerminals(terminalKeys, mergedVideo, mergedAudio);

        WGNodeData template = clipOutputsInOrder[0];
        g.CurrentMedia = new WGNodeData(mergedVideo, g, WGNodeData.DT_VIDEO, template.Compat)
        {
            Width = template.Width,
            Height = template.Height,
            Frames = allFramesKnown ? sumFrames : template.Frames,
            FPS = template.FPS
        };
        if (mergedAudio is not null)
        {
            g.CurrentMedia.AttachedAudio = new WGNodeData(
                mergedAudio,
                g,
                WGNodeData.DT_AUDIO,
                template.AttachedAudio?.Compat ?? g.CurrentAudioVae?.Compat);
        }
    }

    private static string OutputKey(JArray path) => $"{path[0]}::{path[1]}";

    private static string OutputKey(INodeOutput output) => $"{output.Node.Id}::{output.SlotIndex}";

    private JArray TryGetClipConcatenatableAudioPath(WGNodeData clip)
    {
        WGNodeData attached = clip?.AttachedAudio;
        if (attached?.Path is not JArray { Count: 2 } path)
        {
            return null;
        }

        if (attached.DataType == WGNodeData.DT_AUDIO)
        {
            return new JArray(path[0], path[1]);
        }

        if (attached.DataType == WGNodeData.DT_LATENT_AUDIO && g.CurrentAudioVae is not null)
        {
            WGNodeData decoded = attached.DecodeLatents(g.CurrentAudioVae, true);
            if (decoded?.Path is JArray { Count: 2 } dp
                && decoded.DataType == WGNodeData.DT_AUDIO)
            {
                return new JArray(dp[0], dp[1]);
            }
        }

        return null;
    }

    private JArray MergeClipVideosWithBatchImagesNode(IReadOnlyList<JArray> paths)
    {
        List<JArray> layer = [.. paths];

        if (layer.Count == 1)
        {
            return layer[0];
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        try
        {
            while (layer.Count > BatchImagesNodeMaxInputs)
            {
                string chunkNodeId = AddBatchImagesNode(
                    bridge,
                    layer.Take(BatchImagesNodeMaxInputs));
                List<JArray> next = [new JArray(chunkNodeId, 0)];
                for (int i = BatchImagesNodeMaxInputs; i < layer.Count; i++)
                {
                    next.Add(layer[i]);
                }

                layer = next;
            }

            return new JArray(AddBatchImagesNode(bridge, layer), 0);
        }
        finally
        {
            BridgeSync.SyncLastId(g);
        }
    }

    private static string AddBatchImagesNode(WorkflowBridge bridge, IEnumerable<JArray> imagePaths)
    {
        BatchImagesNodeNode node = bridge.AddNode(new BatchImagesNodeNode());
        JObject extra = [];
        int i = 0;
        foreach (JArray imagePath in imagePaths)
        {
            extra[$"images.image{i}"] = new JArray(imagePath[0], imagePath[1]);
            i++;
        }
        node.ExtraInputs = extra;
        bridge.SyncNode(node);
        return node.Id;
    }

    private JArray CascadeAudioConcat(IReadOnlyList<JArray> paths)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput acc = bridge.ResolvePath(paths[0]);
        if (acc is null)
        {
            return paths[0];
        }

        for (int i = 1; i < paths.Count; i++)
        {
            INodeOutput next = bridge.ResolvePath(paths[i]);
            if (next is null)
            {
                continue;
            }
            AudioConcatNode concat = bridge.AddNode(new AudioConcatNode());
            concat.Audio1.ConnectToUntyped(acc);
            concat.Audio2.ConnectToUntyped(next);
            concat.Direction.Set("after");
            bridge.SyncNode(concat);
            acc = concat.AUDIO;
        }
        BridgeSync.SyncLastId(g);

        return new JArray(acc.Node.Id, acc.SlotIndex);
    }

    private void RetargetSwarmSaveAnimationWsForClipTerminals(
        HashSet<string> terminalKeys,
        JArray images,
        JArray audio)
    {
        if (images is not { Count: 2 } || terminalKeys.Count == 0)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput imagesOutput = bridge.ResolvePath(images);
        INodeOutput audioOutput = audio is { Count: 2 } ? bridge.ResolvePath(audio) : null;
        if (imagesOutput is null)
        {
            return;
        }

        foreach (SwarmSaveAnimationWSNode save in
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>())
        {
            if (save.Images.Connection is not INodeOutput existingImages
                || !terminalKeys.Contains(OutputKey(existingImages)))
            {
                continue;
            }

            save.Images.ConnectToUntyped(imagesOutput);
            if (audioOutput is not null)
            {
                save.Audio.ConnectToUntyped(audioOutput);
            }
            else
            {
                save.Audio.Clear();
            }
            bridge.SyncNode(save);
        }
    }
}
