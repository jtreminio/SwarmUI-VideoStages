using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal static class MultiClipParallelWorkflowFlags
{
    internal const string NodeHelperKey = "videostages.parallel-multi-clip";
}

/// <summary>Merges parallel-clip video (and audio when present) via BatchImages/AudioConcat, then retargets SwarmSaveAnimationWS to the merge.</summary>
internal static class MultiClipParallelMerger
{
    public static void Apply(
        WorkflowGenerator g,
        IReadOnlyList<WGNodeData> clipOutputsInOrder,
        JArray parallelRootVideoPath = null)
    {
        if (g is null || clipOutputsInOrder is null || clipOutputsInOrder.Count < 2)
        {
            return;
        }

        List<JArray> videoPaths = [];
        List<JArray> audioPaths = [];
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            if (clip?.Path is not JArray vp || vp.Count != 2)
            {
                continue;
            }

            videoPaths.Add(new JArray(vp[0], vp[1]));
            JArray concatAudioPath = TryGetClipConcatenatableAudioPath(g, clip);
            if (concatAudioPath is not null)
            {
                audioPaths.Add(concatAudioPath);
            }
        }

        if (videoPaths.Count < 2)
        {
            return;
        }

        JArray mergedVideo = MergeClipVideosWithBatchImagesNode(g, videoPaths);
        JArray mergedAudio = audioPaths.Count == videoPaths.Count
            ? CascadeAudioConcat(g, audioPaths)
            : null;

        HashSet<string> retargetKeys = [];
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            if (clip?.Path is not JArray vp || vp.Count != 2)
            {
                continue;
            }

            retargetKeys.Add(TerminalPathKey(vp));
        }

        if (parallelRootVideoPath is { Count: 2 })
        {
            retargetKeys.Add(TerminalPathKey(parallelRootVideoPath));
        }

        RetargetSwarmSaveAnimationWsForClipTerminals(g.Workflow, retargetKeys, mergedVideo, mergedAudio);

        WGNodeData template = clipOutputsInOrder[0];
        int? totalFrames = 0;
        foreach (WGNodeData c in clipOutputsInOrder)
        {
            if (c?.Frames is not int f)
            {
                totalFrames = null;
                break;
            }

            totalFrames = totalFrames.Value + f;
        }
        g.CurrentMedia = new WGNodeData(mergedVideo, g, WGNodeData.DT_VIDEO, template.Compat)
        {
            Width = template.Width,
            Height = template.Height,
            Frames = totalFrames ?? template.Frames,
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

    /// <summary>Uses decoded audio for AudioConcat; decodes <see cref="WGNodeData.DT_LATENT_AUDIO"/> when a VAE is available.</summary>
    private static JArray TryGetClipConcatenatableAudioPath(WorkflowGenerator g, WGNodeData clip)
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

    private const int BatchImagesNodeMaxInputs = 50;

    private static JArray MergeClipVideosWithBatchImagesNode(WorkflowGenerator g, IReadOnlyList<JArray> paths)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        List<JArray> layer = [.. paths];

        while (layer.Count > BatchImagesNodeMaxInputs)
        {
            JObject chunkInputs = new();
            for (int i = 0; i < BatchImagesNodeMaxInputs; i++)
            {
                chunkInputs[$"images.image{i}"] = layer[i];
            }

            string chunkNodeId = g.CreateNode(NodeTypes.BatchImagesNode, chunkInputs);
            layer.RemoveRange(0, BatchImagesNodeMaxInputs);
            layer.Insert(0, new JArray(chunkNodeId, 0));
        }

        if (layer.Count == 1)
        {
            return layer[0];
        }

        JObject inputs = new();
        for (int i = 0; i < layer.Count; i++)
        {
            inputs[$"images.image{i}"] = layer[i];
        }

        string nodeId = g.CreateNode(NodeTypes.BatchImagesNode, inputs);
        return new JArray(nodeId, 0);
    }

    private static JArray CascadeAudioConcat(WorkflowGenerator g, IReadOnlyList<JArray> paths)
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

    private static string TerminalPathKey(JArray path) => $"{path[0]}::{path[1]}";

    private static void RetargetSwarmSaveAnimationWsForClipTerminals(
        JObject workflow,
        HashSet<string> terminalPathKeys,
        JArray images,
        JArray audio)
    {
        if (workflow is null || images is null || images.Count != 2 || terminalPathKeys is null || terminalPathKeys.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, JToken> entry in workflow)
        {
            if (entry.Value is not JObject node)
            {
                continue;
            }

            if ($"{node["class_type"]}" != NodeTypes.SwarmSaveAnimationWS)
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
