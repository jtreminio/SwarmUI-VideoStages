using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.LTX2;

public sealed class AudioInjector(WorkflowGenerator g)
{
    private const int AudioInjectionIdBase = 52300;
    private const int AudioInjectionEnsureFallbackSlot = 50;

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
        JToken lengthFramesAudioSource = ResolveLengthToFramesAudioSource(detection.Audio.Path);
        string lengthToFramesId = CreateLengthToFramesNode(lengthFramesAudioSource, fps);
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
        int fps = new JsonParser(g).ResolveFps();
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

    private JToken ResolveLengthToFramesAudioSource(JToken rawAudioPath)
    {
        if (rawAudioPath is not JArray rawRef || rawRef.Count != 2)
        {
            return rawAudioPath;
        }

        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node
                || $"{node["class_type"]}" != NodeTypes.SwarmEnsureAudio
                || node["inputs"] is not JObject inputs
                || inputs["audio"] is not JArray audioInput
                || audioInput.Count != 2)
            {
                continue;
            }
            if (ConnectionRefsEqual(audioInput, rawRef))
            {
                return new JArray(property.Name, 0);
            }
        }

        if (!IsSwarmLoadAudioB64Output(rawRef))
        {
            return rawAudioPath;
        }

        string ensured = g.CreateNode(NodeTypes.SwarmEnsureAudio, new JObject()
        {
            ["audio"] = rawRef,
            ["target_duration"] = 0.1
        }, g.GetStableDynamicID(AudioInjectionIdBase + AudioInjectionEnsureFallbackSlot, 0));
        return new JArray(ensured, 0);
    }

    private bool IsSwarmLoadAudioB64Output(JArray rawRef)
    {
        string sourceId = $"{rawRef[0]}";
        if (!g.Workflow.TryGetValue(sourceId, out JToken token) || token is not JObject node)
        {
            return false;
        }

        return $"{node["class_type"]}" == NodeTypes.SwarmLoadAudioB64;
    }

    private static bool ConnectionRefsEqual(JToken left, JToken right)
    {
        if (left is not JArray la || right is not JArray ra || la.Count != 2 || ra.Count != 2)
        {
            return false;
        }

        return $"{la[0]}" == $"{ra[0]}" && la[1].Value<int>() == ra[1].Value<int>();
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
        if (RootVideoStageResizer.TryGetConfiguredRootStageResolution(g, out int rootWidth, out int rootHeight))
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
