using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages;

namespace VideoStages.LTX2;

public sealed class AudioInjector(WorkflowGenerator g)
{
    private const int AudioInjectionIdBase = 52300;

    public bool TryInject(AudioStageDetector.Detection detection)
    {
        if (detection?.Audio is null || !g.IsLTXV2() || g.CurrentAudioVae is null)
        {
            return false;
        }

        (List<JArray> audioLatentsToReplace, HashSet<string> removableSourceIds, int? workflowFps) = FindAudioLatentsToReplace();
        if (audioLatentsToReplace.Count == 0)
        {
            return false;
        }

        int fps = ResolveFps(workflowFps);
        string lengthToFramesId = CreateLengthToFramesNode(detection.Audio.Path, fps);
        JArray framesConnection = MakeConnection(lengthToFramesId, 1);
        ApplyFramesConnectionToSources(removableSourceIds, framesConnection);
        ApplyFramesConnectionToVideoLatents(framesConnection);
        WGNodeData adjustedAudio = new(new JArray(lengthToFramesId, 0), g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
        WGNodeData encodedAudio = adjustedAudio.EncodeToLatent(g.CurrentAudioVae);
        string setMaskId = CreateAudioMaskNode(encodedAudio.Path);
        ReplaceAudioLatentConnections(audioLatentsToReplace, setMaskId);
        RemoveUnusedSourceNodes(removableSourceIds);
        return true;
    }

    private (List<JArray> AudioLatentsToReplace, HashSet<string> RemovableSourceIds, int? WorkflowFps) FindAudioLatentsToReplace()
    {
        List<JArray> audioLatentsToReplace = [];
        HashSet<string> removableSourceIds = [];
        int? workflowFps = null;
        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node
                || $"{node["class_type"]}" != NodeTypes.LTXVConcatAVLatent
                || node["inputs"] is not JObject inputs
                || inputs["audio_latent"] is not JArray audioLatent
                || audioLatent.Count != 2)
            {
                continue;
            }
            string sourceId = $"{audioLatent[0]}";
            if (!g.Workflow.TryGetValue(sourceId, out JToken sourceToken)
                || sourceToken is not JObject sourceNode
                || $"{sourceNode["class_type"]}" != NodeTypes.LTXVEmptyLatentAudio)
            {
                continue;
            }
            audioLatentsToReplace.Add(audioLatent);
            removableSourceIds.Add(sourceId);
            workflowFps ??= ReadFrameRate(sourceNode["inputs"] as JObject);
        }
        return (audioLatentsToReplace, removableSourceIds, workflowFps);
    }

    private int ResolveFps(int? workflowFps)
    {
        int fps = workflowFps ?? g.Text2VideoFPS();
        if (fps <= 0)
        {
            fps = g.UserInput.Get(T2IParamTypes.VideoFPS, 24);
        }
        if (fps <= 0)
        {
            fps = 24;
        }
        return fps;
    }

    private string CreateLengthToFramesNode(JToken audioPath, int fps)
    {
        return g.CreateNode(NodeTypes.AudioLengthToFrames, new JObject()
        {
            ["audio"] = audioPath,
            ["frame_rate"] = fps
        }, g.GetStableDynamicID(AudioInjectionIdBase + 100, 0));
    }

    private void ApplyFramesConnectionToSources(HashSet<string> sourceIds, JArray framesConnection)
    {
        foreach (string sourceId in sourceIds)
        {
            if (g.Workflow[sourceId] is not JObject emptyNode || emptyNode["inputs"] is not JObject emptyInputs)
            {
                continue;
            }
            SetFrameCountInput(emptyInputs, framesConnection);
        }
    }

    private void ApplyFramesConnectionToVideoLatents(JArray framesConnection)
    {
        g.RunOnNodesOfClass(NodeTypes.EmptyLTXVLatentVideo, (_, videoData) =>
        {
            if (videoData["inputs"] is JObject videoInputs)
            {
                videoInputs["length"] = CloneConnection(framesConnection);
            }
        });
    }

    private string CreateAudioMaskNode(JToken encodedAudioPath)
    {
        string solidMaskId = g.CreateNode(NodeTypes.SolidMask, new JObject()
        {
            ["value"] = 0.0,
            ["width"] = g.UserInput.GetImageWidth(),
            ["height"] = g.UserInput.GetImageHeight()
        }, g.GetStableDynamicID(AudioInjectionIdBase + 200, 0));
        return g.CreateNode(NodeTypes.SetLatentNoiseMask, new JObject()
        {
            ["samples"] = encodedAudioPath,
            ["mask"] = MakeConnection(solidMaskId, 0)
        }, g.GetStableDynamicID(AudioInjectionIdBase + 300, 0));
    }

    private static void ReplaceAudioLatentConnections(List<JArray> audioLatents, string setMaskId)
    {
        foreach (JArray audioLatent in audioLatents)
        {
            audioLatent[0] = setMaskId;
            audioLatent[1] = 0;
        }
    }

    private void RemoveUnusedSourceNodes(HashSet<string> removableSourceIds)
    {
        g.UsedInputs = null;
        foreach (string sourceId in removableSourceIds)
        {
            if (!g.NodeIsConnectedAnywhere(sourceId))
            {
                g.Workflow.Remove(sourceId);
            }
        }
    }

    private static JArray MakeConnection(string nodeId, int outputIndex)
    {
        return new JArray(nodeId, outputIndex);
    }

    private static JArray CloneConnection(JArray connection)
    {
        return new JArray(connection[0], connection[1]);
    }

    private static int? ReadFrameRate(JObject inputs)
    {
        if (inputs is null)
        {
            return null;
        }

        return inputs.Value<int?>("frame_rate")
            ?? (inputs.Value<double?>("frame_rate") is double frameRate ? (int?)Math.Round(frameRate) : null)
            ?? inputs.Value<int?>("fps")
            ?? (inputs.Value<double?>("fps") is double fps ? (int?)Math.Round(fps) : null);
    }

    private static void SetFrameCountInput(JObject inputs, JArray framesConnection)
    {
        if (inputs.ContainsKey("frames_number"))
        {
            inputs["frames_number"] = CloneConnection(framesConnection);
            return;
        }
        if (inputs.ContainsKey("length"))
        {
            inputs["length"] = CloneConnection(framesConnection);
            return;
        }

        inputs["frames_number"] = CloneConnection(framesConnection);
    }
}
