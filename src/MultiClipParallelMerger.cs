using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal sealed class MultiClipParallelMerger(WorkflowGenerator g)
{
    internal const string NodeHelperKey = "videostages.parallel-multi-clip";
    private const int BatchImagesNodeMaxInputs = 50;

    private static string TerminalPathKey(JArray path) => $"{path[0]}::{path[1]}";

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
        HashSet<string> retargetKeys = [];
        int sumFrames = 0;
        bool allFramesKnown = true;
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            if (clip?.Path is JArray vp && vp.Count == 2)
            {
                videoPaths.Add(new JArray(vp[0], vp[1]));
                retargetKeys.Add(TerminalPathKey(vp));
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
            retargetKeys.Add(TerminalPathKey(parallelRootVideoPath));
        }

        RetargetSwarmSaveAnimationWsForClipTerminals(g.Workflow, retargetKeys, mergedVideo, mergedAudio);
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
        JArray acc = paths[0];
        for (int i = 1; i < paths.Count; i++)
        {
            string nodeId = g.CreateNode(NodeTypes.AudioConcat, new JObject()
            {
                ["audio1"] = acc,
                ["audio2"] = paths[i],
                ["direction"] = "after"
            });
            acc = new JArray(nodeId, 0);
        }

        return acc;
    }

    private static void RetargetSwarmSaveAnimationWsForClipTerminals(
        JObject workflow,
        HashSet<string> terminalPathKeys,
        JArray images,
        JArray audio)
    {
        if (workflow is null
            || images is null
            || images.Count != 2
            || terminalPathKeys is null
            || terminalPathKeys.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, JToken> entry in workflow)
        {
            if (entry.Value is not JObject node)
            {
                continue;
            }

            if (!StringUtils.NodeTypeMatches(node, NodeTypes.SwarmSaveAnimationWS))
            {
                continue;
            }

            if (node["inputs"] is not JObject inputs
                || inputs["images"] is not JArray imgRef
                || imgRef.Count != 2)
            {
                continue;
            }

            if (!terminalPathKeys.Contains(TerminalPathKey(imgRef)))
            {
                continue;
            }

            inputs["images"] = images;
            if (audio is not null && audio.Count == 2)
            {
                inputs["audio"] = audio;
            }
            else
            {
                inputs.Remove("audio");
            }
        }
    }
}
