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
            if (clip?.Path is JArray vp && vp.Count == 2)
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
        if (attached?.Path is not JArray path || path.Count != 2)
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
            if (decoded?.Path is JArray dp
                && dp.Count == 2
                && decoded.DataType == WGNodeData.DT_AUDIO)
            {
                return new JArray(dp[0], dp[1]);
            }
        }

        return null;
    }

    /// <summary>
    /// BatchImagesNode takes a dynamic list of inputs (`images.image0`, `images.image1`, …) that
    /// the ComfyTyped codegen flattens to a single `Images` field, so it can't round-trip through
    /// the typed bridge. Build it via <see cref="WorkflowGenerator.CreateNode"/> directly.
    /// </summary>
    private JArray MergeClipVideosWithBatchImagesNode(IReadOnlyList<JArray> paths)
    {
        List<JArray> layer = [.. paths];

        while (layer.Count > BatchImagesNodeMaxInputs)
        {
            JObject chunkInputs = [];
            for (int i = 0; i < BatchImagesNodeMaxInputs; i++)
            {
                chunkInputs[$"images.image{i}"] = layer[i];
            }

            string chunkNodeId = g.CreateNode(NodeTypes.BatchImagesNode, chunkInputs);
            List<JArray> next = [new JArray(chunkNodeId, 0)];
            for (int i = BatchImagesNodeMaxInputs; i < layer.Count; i++)
            {
                next.Add(layer[i]);
            }

            layer = next;
        }

        if (layer.Count == 1)
        {
            return layer[0];
        }

        JObject inputs = [];
        for (int i = 0; i < layer.Count; i++)
        {
            inputs[$"images.image{i}"] = layer[i];
        }

        return new JArray(g.CreateNode(NodeTypes.BatchImagesNode, inputs), 0);
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
        if (images is not { Count: 2 } || terminalKeys is null || terminalKeys.Count == 0)
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

        foreach (SwarmSaveAnimationWSNode save in bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>())
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
