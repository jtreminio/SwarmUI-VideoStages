using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

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
        if (detection?.Audio is null || !g.IsLTXV2() || g.CurrentAudioVae is null)
        {
            return false;
        }

        (
            List<JArray> audioLatentsToReplace,
            HashSet<string> removableSourceIds,
            int? workflowFps) = FindAudioLatentsToReplace();
        if (audioLatentsToReplace.Count == 0)
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
            string lengthToFramesId = CreateLengthToFramesNode(lengthFramesAudioSource, fps);
            JArray framesConnection = MakeConnection(lengthToFramesId, 1);
            ApplyFramesConnectionToSources(removableSourceIds, framesConnection);
            ApplyFramesConnectionToVideoLatents(framesConnection);
            adjustedAudio = new(new JArray(lengthToFramesId, 0), g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
        }
        WGNodeData encodedAudio = adjustedAudio.EncodeToLatent(g.CurrentAudioVae);
        string setMaskId = CreateAudioMaskNode(encodedAudio.Path);
        ReplaceAudioLatentConnections(audioLatentsToReplace, setMaskId);
        RemoveUnusedSourceNodes(removableSourceIds);
        return true;
    }

    private (List<JArray> AudioLatentsToReplace, HashSet<string> RemovableSourceIds, int? WorkflowFps)
        FindAudioLatentsToReplace()
    {
        List<JArray> audioLatentsToReplace = [];
        HashSet<string> removableSourceIds = [];
        int? workflowFps = null;
        foreach (WorkflowNode concat in WorkflowUtils.NodesOfType(g.Workflow, LtxNodeTypes.LTXVConcatAVLatent))
        {
            JObject node = concat.Node;
            if (node["inputs"] is not JObject inputs
                || inputs["audio_latent"] is not JArray audioLatent
                || audioLatent.Count != 2)
            {
                continue;
            }
            string sourceId = $"{audioLatent[0]}";
            if (!g.Workflow.TryGetValue(sourceId, out JToken sourceToken)
                || sourceToken is not JObject sourceNode
                || !StringUtils.NodeTypeMatches(sourceNode, LtxNodeTypes.LTXVEmptyLatentAudio))
            {
                continue;
            }
            audioLatentsToReplace.Add(audioLatent);
            removableSourceIds.Add(sourceId);
            if (sourceNode["inputs"] is JObject rateInputs)
            {
                workflowFps ??= ReadFrameRate(rateInputs);
            }
        }
        return (audioLatentsToReplace, removableSourceIds, workflowFps);
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
        g.RunOnNodesOfClass(LtxNodeTypes.EmptyLTXVLatentVideo, (_, videoData) =>
        {
            if (videoData["inputs"] is not JObject videoInputs)
            {
                return;
            }
            videoInputs["length"] = CloneConnection(framesConnection);
        });
    }

    private string CreateAudioMaskNode(JToken encodedAudioPath)
    {
        int width = g.UserInput.GetImageWidth();
        int height = g.UserInput.GetImageHeight();
        if (rootVideoStageResizer.TryGetConfiguredRootStageResolution(out int rootWidth, out int rootHeight))
        {
            width = rootWidth;
            height = rootHeight;
        }

        string solidMaskId = g.CreateNode(NodeTypes.SolidMask, new JObject()
        {
            ["value"] = 0.0,
            ["width"] = width,
            ["height"] = height
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

    private static int? ReadIntOrRoundedDouble(JObject inputs, string key)
    {
        return inputs.Value<int?>(key)
            ?? (inputs.Value<double?>(key) is double d ? (int?)Math.Round(d) : null);
    }

    private static int? ReadFrameRate(JObject inputs)
    {
        if (inputs is null)
        {
            return null;
        }

        return ReadIntOrRoundedDouble(inputs, "frame_rate")
            ?? ReadIntOrRoundedDouble(inputs, "fps");
    }

    private static void SetFrameCountInput(JObject inputs, JArray framesConnection)
    {
        JArray wired = CloneConnection(framesConnection);
        string key;
        if (inputs.ContainsKey("frames_number"))
        {
            key = "frames_number";
        }
        else if (inputs.ContainsKey("length"))
        {
            key = "length";
        }
        else
        {
            key = "frames_number";
        }
        inputs[key] = wired;
    }
}
