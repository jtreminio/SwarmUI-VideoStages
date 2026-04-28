using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.LTX2;

internal sealed class LtxPostVideoChain
{
    private readonly WorkflowGenerator g;
    private readonly bool useReusedAudioLatent;
    public readonly WGNodeData CurrentOutputMedia;
    public readonly JArray AvLatentPath;
    public readonly JArray AudioLatentPath;
    public readonly JArray VideoVaePath;
    public readonly JArray AudioVaePath;
    public readonly string VideoDecodeNodeId;
    public readonly string AudioDecodeNodeId;
    public readonly JArray DecodeOutputPath;
    public readonly bool HasPostDecodeWrappers;

    private LtxPostVideoChain(
        WorkflowGenerator generator,
        WGNodeData currentOutputMedia,
        JArray avLatentPath,
        JArray audioLatentPath,
        JArray videoVaePath,
        JArray audioVaePath,
        string videoDecodeNodeId,
        string audioDecodeNodeId,
        JArray decodeOutputPath,
        bool hasPostDecodeWrappers,
        bool useReusedAudio)
    {
        g = generator;
        useReusedAudioLatent = useReusedAudio;
        CurrentOutputMedia = currentOutputMedia;
        AvLatentPath = avLatentPath;
        AudioLatentPath = audioLatentPath;
        VideoVaePath = videoVaePath;
        AudioVaePath = audioVaePath;
        VideoDecodeNodeId = videoDecodeNodeId;
        AudioDecodeNodeId = audioDecodeNodeId;
        DecodeOutputPath = decodeOutputPath;
        HasPostDecodeWrappers = hasPostDecodeWrappers;
    }

    public static LtxPostVideoChain TryCapture(WorkflowGenerator generator) =>
        TryCapture(generator, null, mutateReuseAudioState: false);

    public static LtxPostVideoChain TryCapture(WorkflowGenerator generator, JsonParser.StageSpec stage) =>
        TryCapture(generator, stage, mutateReuseAudioState: true);

    private static LtxPostVideoChain TryCapture(
        WorkflowGenerator generator,
        JsonParser.StageSpec stage,
        bool mutateReuseAudioState)
    {
        bool clipCanReuseAudio = stage?.ClipReuseAudio == true && stage.ClipStageCount >= 3;
        bool useReusedAudio = clipCanReuseAudio && stage.ClipStageIndex > 0;
        bool captureReusableAudio = clipCanReuseAudio && stage.ClipStageIndex == 1;
        if (mutateReuseAudioState && !useReusedAudio && !captureReusableAudio)
        {
            LtxAudioReuseState.Clear(generator);
        }

        if (generator.CurrentMedia?.IsRawMedia != true
            || generator.CurrentMedia.Path is not JArray mediaPath
            || mediaPath.Count != 2)
        {
            return null;
        }

        if (!WorkflowUtils.TryResolveNearestUpstreamDecode(generator.Workflow, mediaPath, out WorkflowNode decode))
        {
            return null;
        }

        if (!StringUtils.NodeTypeMatches(decode.Node, NodeTypes.VAEDecode)
            && !StringUtils.NodeTypeMatches(decode.Node, NodeTypes.VAEDecodeTiled))
        {
            return null;
        }

        JObject decodeInputs = decode.Node["inputs"] as JObject;
        JArray samplesRef = LtxVaeDecodeInputs.TryGetDecodeSamplesRef(decodeInputs);
        if (samplesRef is null || samplesRef.Count != 2)
        {
            return null;
        }

        JArray videoVaeRef = decode.Node["inputs"]?["vae"] as JArray;
        if (videoVaeRef is null || videoVaeRef.Count != 2)
        {
            return null;
        }

        string separateId = $"{samplesRef[0]}";
        if (!generator.Workflow.TryGetValue(separateId, out JToken separateToken)
            || separateToken is not JObject separateNode
            || !StringUtils.NodeTypeMatches(separateNode, LtxNodeTypes.LTXVSeparateAVLatent))
        {
            return null;
        }

        if (separateNode["inputs"]?["av_latent"] is not JArray avLatentRef || avLatentRef.Count != 2)
        {
            return null;
        }

        if (!TryFindAudioDecode(generator.Workflow, separateId, out string audioDecodeId, out JArray audioVaeRef)
            && !TryResolveCurrentAudioVae(generator, out audioVaeRef))
        {
            return null;
        }

        if (mutateReuseAudioState && captureReusableAudio)
        {
            LtxAudioReuseState.Remember(generator, new JArray(separateId, 1));
        }

        return new LtxPostVideoChain(
            generator,
            CloneMedia(generator, generator.CurrentMedia),
            new JArray(avLatentRef[0], avLatentRef[1]),
            new JArray(separateId, 1),
            new JArray(videoVaeRef[0], videoVaeRef[1]),
            new JArray(audioVaeRef[0], audioVaeRef[1]),
            decode.Id,
            audioDecodeId,
            new JArray(decode.Id, 0),
            !JToken.DeepEquals(mediaPath, new JArray(decode.Id, 0)),
            useReusedAudio);
    }

    public WGNodeData CreateStageInput()
    {
        WGNodeData stageInput = new(AvLatentPath, g, WGNodeData.DT_LATENT_AUDIOVIDEO, ResolveVideoCompat())
        {
            Width = CurrentOutputMedia.Width,
            Height = CurrentOutputMedia.Height,
            Frames = CurrentOutputMedia.Frames,
            FPS = CurrentOutputMedia.FPS
        };
        AttachSourceAudio(stageInput);
        return stageInput;
    }

    public WGNodeData CreateStageInputVae()
    {
        return new WGNodeData(new JArray(VideoVaePath[0], VideoVaePath[1]), g, WGNodeData.DT_VAE, ResolveVideoCompat());
    }

    public bool CanReuseCurrentOutputAsStageInput(WGNodeData sourceMedia)
    {
        return !HasPostDecodeWrappers
            && sourceMedia?.Path is JArray sourcePath
            && JToken.DeepEquals(sourcePath, CurrentOutputMedia.Path);
    }

    public WGNodeData CreateDetachedGuideMedia(WGNodeData vae)
    {
        if (vae?.Path is not JArray vaePath || vaePath.Count != 2)
        {
            WGNodeData detachedCurrent = CloneMedia(g, CurrentOutputMedia);
            AttachSourceAudio(detachedCurrent);
            return detachedCurrent;
        }

        string detachedSeparate = g.CreateNode(LtxNodeTypes.LTXVSeparateAVLatent, new JObject()
        {
            ["av_latent"] = new JArray(AvLatentPath[0], AvLatentPath[1])
        });
        string detachedDecode = g.CreateNode(FinalVideoDecodeNodeType(), (_, n) =>
        {
            n["inputs"] = CreateFinalDecodeInputs(vaePath, new JArray(detachedSeparate, 0));
        });
        WGNodeData detachedGuide = new(new JArray(detachedDecode, 0), g, WGNodeData.DT_VIDEO, vae.Compat)
        {
            Width = CurrentOutputMedia.Width,
            Height = CurrentOutputMedia.Height,
            Frames = CurrentOutputMedia.Frames,
            FPS = CurrentOutputMedia.FPS
        };
        AttachSourceAudio(detachedGuide);
        return detachedGuide;
    }

    public void AttachSourceAudio(WGNodeData media)
    {
        if (media is null)
        {
            return;
        }

        if (IsExplicitUploadAudio(media.AttachedAudio))
        {
            return;
        }

        WGNodeData sourceAudio = CreateSourceAudioReference();
        if (sourceAudio is null)
        {
            return;
        }

        if (media.AttachedAudio?.Path is JArray existingAudioPath
            && existingAudioPath.Count == 2
            && media.AttachedAudio.DataType == sourceAudio.DataType
            && JToken.DeepEquals(existingAudioPath, sourceAudio.Path))
        {
            return;
        }

        media.AttachedAudio = sourceAudio;
    }

    public void SpliceCurrentOutput(WGNodeData vae)
    {
        if (g.CurrentMedia?.Path is not JArray stageOutputPath || stageOutputPath.Count != 2)
        {
            return;
        }

        string newSeparate = g.CreateNode(LtxNodeTypes.LTXVSeparateAVLatent, new JObject()
        {
            ["av_latent"] = stageOutputPath
        });

        RetargetVideoDecode(VideoDecodeNodeId, vae?.Path ?? g.CurrentVae?.Path, new JArray(newSeparate, 0));
        int retargetedAudioDecodes = WorkflowUtils.RetargetInputConnections(
            g.Workflow,
            new JArray($"{AudioLatentPath[0]}", AudioLatentPath[1]),
            new JArray(newSeparate, 1),
            connection => connection.NodeId == AudioDecodeNodeId && connection.InputName == "samples");
        if (retargetedAudioDecodes == 0)
        {
            RetargetCapturedAudioDecode(new JArray(newSeparate, 1));
        }
        g.CurrentMedia = CloneMedia(g, CurrentOutputMedia);
        AttachSourceAudio(g.CurrentMedia);
    }

    public void SpliceCurrentOutputToDedicatedBranch(
        WGNodeData vae,
        int outputWidth,
        int outputHeight,
        int? outputFrames,
        int? outputFps)
    {
        if (g.CurrentMedia?.Path is not JArray stageOutputPath || stageOutputPath.Count != 2)
        {
            return;
        }

        string newSeparate = g.CreateNode(LtxNodeTypes.LTXVSeparateAVLatent, new JObject()
        {
            ["av_latent"] = stageOutputPath
        });

        JArray vaeRef = vae?.Path ?? g.CurrentVae?.Path;
        if (vaeRef is null || vaeRef.Count != 2)
        {
            return;
        }

        string dedicatedVideoDecode = g.CreateNode(FinalVideoDecodeNodeType(), (_, n) =>
        {
            n["inputs"] = CreateFinalDecodeInputs(vaeRef, new JArray(newSeparate, 0));
        });
        string dedicatedAudioDecode = g.CreateNode(LtxNodeTypes.LTXVAudioVAEDecode, new JObject()
        {
            ["audio_vae"] = new JArray(AudioVaePath[0], AudioVaePath[1]),
            ["samples"] = new JArray(newSeparate, 1)
        });

        WGNodeData decodedVideo = new(
            new JArray(dedicatedVideoDecode, 0),
            g,
            WGNodeData.DT_VIDEO,
            ResolveVideoCompat())
        {
            Width = outputWidth,
            Height = outputHeight,
            Frames = outputFrames ?? CurrentOutputMedia.Frames,
            FPS = outputFps ?? CurrentOutputMedia.FPS
        };
        WGNodeData decodedAudio = new(
            new JArray(dedicatedAudioDecode, 0),
            g,
            WGNodeData.DT_AUDIO,
            ResolveAudioCompat());
        decodedVideo.AttachedAudio = decodedAudio;
        g.CurrentMedia = decodedVideo;
    }

    public void RetargetAnimationSaves(JArray newImagePath)
    {
        if (newImagePath is null || newImagePath.Count != 2)
        {
            return;
        }

        _ = WorkflowUtils.RetargetInputConnections(
            g.Workflow,
            CurrentOutputMedia.Path,
            newImagePath,
            connection =>
            {
                if (!StringUtils.Equals(connection.InputName, "images"))
                {
                    return false;
                }
                if (g.Workflow[connection.NodeId] is not JObject node)
                {
                    return false;
                }
                return StringUtils.NodeTypeMatches(node, NodeTypes.SwarmSaveAnimationWS);
            });
    }

    private void RetargetCapturedAudioDecode(JArray newSamplesPath)
    {
        if (string.IsNullOrWhiteSpace(AudioDecodeNodeId)
            || newSamplesPath is null
            || newSamplesPath.Count != 2
            || g.Workflow[AudioDecodeNodeId] is not JObject audioDecode
            || !StringUtils.NodeTypeMatches(audioDecode, LtxNodeTypes.LTXVAudioVAEDecode))
        {
            return;
        }

        JObject inputs = audioDecode["inputs"] as JObject;
        if (inputs is null)
        {
            inputs = [];
            audioDecode["inputs"] = inputs;
        }
        inputs["samples"] = new JArray(newSamplesPath[0], newSamplesPath[1]);
    }

    internal static bool TryFindAudioDecode(
        JObject workflow,
        string separateId,
        out string audioDecodeId,
        out JArray audioVaeRef)
    {
        audioDecodeId = null;
        audioVaeRef = null;
        foreach (KeyValuePair<string, JToken> entry in workflow)
        {
            if (entry.Value is not JObject node)
            {
                continue;
            }
            if (!StringUtils.NodeTypeMatches(node, LtxNodeTypes.LTXVAudioVAEDecode))
            {
                continue;
            }
            if (node["inputs"]?["samples"] is not JArray samplesRef || samplesRef.Count != 2)
            {
                continue;
            }
            if ($"{samplesRef[0]}" == separateId && $"{samplesRef[1]}" == "1")
            {
                if (node["inputs"]?["audio_vae"] is not JArray foundAudioVaeRef || foundAudioVaeRef.Count != 2)
                {
                    continue;
                }

                audioDecodeId = entry.Key;
                audioVaeRef = new JArray(foundAudioVaeRef[0], foundAudioVaeRef[1]);
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveCurrentAudioVae(WorkflowGenerator generator, out JArray audioVaeRef)
    {
        audioVaeRef = null;
        if (generator?.CurrentAudioVae?.Path is not JArray currentAudioVaePath || currentAudioVaePath.Count != 2)
        {
            return false;
        }

        audioVaeRef = new JArray(currentAudioVaePath[0], currentAudioVaePath[1]);
        return true;
    }

    private static WGNodeData CloneMedia(WorkflowGenerator generator, WGNodeData media)
    {
        if (media?.Path is not JArray path || path.Count != 2)
        {
            return null;
        }

        WGNodeData cloned = new(new JArray(path[0], path[1]), generator, media.DataType, media.Compat)
        {
            Width = media.Width,
            Height = media.Height,
            Frames = media.Frames,
            FPS = media.FPS
        };
        if (media.AttachedAudio?.Path is JArray audioPath && audioPath.Count == 2)
        {
            cloned.AttachedAudio = new WGNodeData(
                new JArray(audioPath[0], audioPath[1]),
                generator,
                media.AttachedAudio.DataType,
                media.AttachedAudio.Compat)
            {
                Width = media.AttachedAudio.Width,
                Height = media.AttachedAudio.Height,
                Frames = media.AttachedAudio.Frames,
                FPS = media.AttachedAudio.FPS
            };
        }
        return cloned;
    }

    private WGNodeData CreateSourceAudioReference()
    {
        if (useReusedAudioLatent && LtxAudioReuseState.TryGetPath(g, out JArray reusedAudioLatentPath))
        {
            return new WGNodeData(
                reusedAudioLatentPath,
                g,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (IsExplicitUploadAudio(CurrentOutputMedia?.AttachedAudio))
        {
            JArray currentAudioLatentPath = new(AudioLatentPath[0], AudioLatentPath[1]);
            if (CurrentOutputMedia.AttachedAudio?.Path is JArray explicitUploadPath
                && explicitUploadPath.Count == 2
                && IsAudioLatentDerivedFromUpload(currentAudioLatentPath, $"{explicitUploadPath[0]}"))
            {
                return new WGNodeData(
                    currentAudioLatentPath,
                    g,
                    WGNodeData.DT_LATENT_AUDIO,
                    ResolveAudioCompat());
            }
            return CloneAudioReference(CurrentOutputMedia.AttachedAudio);
        }

        return new WGNodeData(
            new JArray(AudioLatentPath[0], AudioLatentPath[1]),
            g,
            WGNodeData.DT_LATENT_AUDIO,
            ResolveAudioCompat());
    }

    private bool IsAudioLatentDerivedFromUpload(JArray audioLatentPath, string uploadNodeId)
    {
        if (audioLatentPath is null
            || audioLatentPath.Count != 2
            || string.IsNullOrWhiteSpace(uploadNodeId))
        {
            return false;
        }

        return WalkUpstreamFromAudioOutputRef(
            audioLatentPath,
            tryMatchNodeId: id => StringUtils.Equals(id, uploadNodeId),
            tryMatchNode: null);
    }

    private WGNodeData CloneAudioReference(WGNodeData audio)
    {
        return new WGNodeData(
            new JArray(audio.Path[0], audio.Path[1]),
            g,
            audio.DataType,
            audio.Compat)
        {
            Width = audio.Width,
            Height = audio.Height,
            Frames = audio.Frames,
            FPS = audio.FPS
        };
    }

    private bool IsExplicitUploadAudio(WGNodeData audio)
    {
        if (audio?.DataType != WGNodeData.DT_AUDIO
            || audio.Path is not JArray audioPath
            || audioPath.Count != 2)
        {
            return false;
        }

        return AudioPathTracesToNodeType(audioPath, NodeTypes.SwarmLoadAudioB64);
    }

    private bool AudioPathTracesToNodeType(JArray audioPath, string classType)
    {
        if (audioPath is null
            || audioPath.Count != 2
            || string.IsNullOrWhiteSpace(classType))
        {
            return false;
        }

        return WalkUpstreamFromAudioOutputRef(
            audioPath,
            tryMatchNodeId: null,
            tryMatchNode: node => StringUtils.NodeTypeMatches(node, classType));
    }

    private bool WalkUpstreamFromAudioOutputRef(
        JArray audioPath,
        Func<string, bool> tryMatchNodeId,
        Func<JObject, bool> tryMatchNode)
    {
        if (audioPath is null || audioPath.Count != 2)
        {
            return false;
        }

        Queue<string> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue($"{audioPath[0]}");
        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (!visited.Add(nodeId))
            {
                continue;
            }

            if (tryMatchNodeId is not null && tryMatchNodeId(nodeId))
            {
                return true;
            }

            if (!g.Workflow.TryGetValue(nodeId, out JToken token)
                || token is not JObject node)
            {
                continue;
            }

            if (tryMatchNode is not null && tryMatchNode(node))
            {
                return true;
            }

            if (node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (JProperty input in inputs.Properties())
            {
                if (input.Value is not JArray inputPath || inputPath.Count != 2)
                {
                    continue;
                }

                string upstreamId = $"{inputPath[0]}";
                if (!string.IsNullOrWhiteSpace(upstreamId))
                {
                    pending.Enqueue(upstreamId);
                }
            }
        }

        return false;
    }

    private void RetargetVideoDecode(string videoDecodeNodeId, JArray vaeRef, JArray latentRef)
    {
        if (string.IsNullOrWhiteSpace(videoDecodeNodeId)
            || vaeRef is null
            || latentRef is null
            || !g.Workflow.TryGetValue(videoDecodeNodeId, out JToken decodeToken)
            || decodeToken is not JObject decodeNode)
        {
            return;
        }

        decodeNode["class_type"] = FinalVideoDecodeNodeType();
        decodeNode["inputs"] = CreateFinalDecodeInputs(vaeRef, latentRef);
    }

    private string FinalVideoDecodeNodeType()
    {
        return ShouldUseTiledVaeDecode() ? NodeTypes.VAEDecodeTiled : NodeTypes.VAEDecode;
    }

    private bool ShouldUseTiledVaeDecode()
    {
        return g.UserInput.TryGet(T2IParamTypes.VAETileSize, out _);
    }

    private JObject CreateFinalDecodeInputs(JArray vaeRef, JArray latentRef)
    {
        if (ShouldUseTiledVaeDecode())
        {
            return CreateFinalTiledDecodeInputs(vaeRef, latentRef);
        }

        return new JObject()
        {
            ["vae"] = new JArray(vaeRef[0], vaeRef[1]),
            ["samples"] = new JArray(latentRef[0], latentRef[1])
        };
    }

    private JObject CreateFinalTiledDecodeInputs(JArray vaeRef, JArray latentRef)
    {
        return new JObject()
        {
            ["vae"] = new JArray(vaeRef[0], vaeRef[1]),
            ["samples"] = new JArray(latentRef[0], latentRef[1]),
            ["tile_size"] = g.UserInput.Get(T2IParamTypes.VAETileSize, 768),
            ["overlap"] = g.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
            ["temporal_size"] = g.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 4096),
            ["temporal_overlap"] = g.UserInput.Get(T2IParamTypes.VAETemporalTileOverlap, 4)
        };
    }

    private T2IModelCompatClass ResolveVideoCompat()
    {
        return T2IModelClassSorter.CompatLtxv2
            ?? g.CurrentVae?.Compat
            ?? CurrentOutputMedia?.Compat
            ?? g.CurrentCompat();
    }

    private T2IModelCompatClass ResolveAudioCompat()
    {
        return g.CurrentAudioVae?.Compat ?? ResolveVideoCompat();
    }
}
