using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages;

namespace VideoStages.LTX2;

/// <summary>
/// Captures and retargets the native LTX post-video decode chain so VideoStages can
/// insert additional samplers before the existing decode/save nodes instead of
/// appending a second final video save.
/// </summary>
internal sealed class PostVideoChain
{
    private const string OriginalAudioLatentNodeHelperKey = "videostages.original-audio-latent";

    private readonly WorkflowGenerator _generator;

    public readonly WGNodeData CurrentOutputMedia;
    public readonly JArray AvLatentPath;
    public readonly JArray AudioLatentPath;
    public readonly JArray VideoVaePath;
    public readonly JArray AudioVaePath;
    public readonly string VideoDecodeNodeId;
    public readonly string AudioDecodeNodeId;
    public readonly JArray DecodeOutputPath;
    public readonly bool HasPostDecodeWrappers;

    private PostVideoChain(
        WorkflowGenerator generator,
        WGNodeData currentOutputMedia,
        JArray avLatentPath,
        JArray audioLatentPath,
        JArray videoVaePath,
        JArray audioVaePath,
        string videoDecodeNodeId,
        string audioDecodeNodeId,
        JArray decodeOutputPath,
        bool hasPostDecodeWrappers)
    {
        _generator = generator;
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

    public static PostVideoChain TryCapture(WorkflowGenerator generator)
    {
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

        string decodeType = $"{decode.Node["class_type"]}";
        if (decodeType != NodeTypes.VAEDecode
            && decodeType != NodeTypes.VAEDecodeTiled)
        {
            return null;
        }

        JArray samplesRef = decode.Node["inputs"]?["samples"] as JArray
            ?? decode.Node["inputs"]?["latent"] as JArray
            ?? decode.Node["inputs"]?["latents"] as JArray;
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
            || $"{separateNode["class_type"]}" != NodeTypes.LTXVSeparateAVLatent)
        {
            return null;
        }

        if (separateNode["inputs"]?["av_latent"] is not JArray avLatentRef || avLatentRef.Count != 2)
        {
            return null;
        }

        if (!TryFindAudioDecode(generator.Workflow, separateId, out string audioDecodeId, out JArray audioVaeRef))
        {
            return null;
        }

        RememberOriginalAudioLatent(generator, new JArray(separateId, 1));

        return new PostVideoChain(
            generator,
            CloneMedia(generator, generator.CurrentMedia),
            new JArray(avLatentRef[0], avLatentRef[1]),
            new JArray(separateId, 1),
            new JArray(videoVaeRef[0], videoVaeRef[1]),
            new JArray(audioVaeRef[0], audioVaeRef[1]),
            decode.Id,
            audioDecodeId,
            new JArray(decode.Id, 0),
            !JToken.DeepEquals(mediaPath, new JArray(decode.Id, 0)));
    }

    public WGNodeData CreateStageInput()
    {
        WGNodeData stageInput = new(AvLatentPath, _generator, WGNodeData.DT_LATENT_AUDIOVIDEO, ResolveVideoCompat())
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
        if (VideoVaePath is null || VideoVaePath.Count != 2)
        {
            return _generator.CurrentVae;
        }

        return new WGNodeData(new JArray(VideoVaePath[0], VideoVaePath[1]), _generator, WGNodeData.DT_VAE, ResolveVideoCompat());
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
            WGNodeData detachedCurrent = CloneMedia(_generator, CurrentOutputMedia);
            AttachSourceAudio(detachedCurrent);
            return detachedCurrent;
        }

        string detachedSeparate = _generator.CreateNode(NodeTypes.LTXVSeparateAVLatent, new JObject()
        {
            ["av_latent"] = new JArray(AvLatentPath[0], AvLatentPath[1])
        });
        string detachedDecode = _generator.CreateNode(NodeTypes.VAEDecodeTiled, (_, n) =>
        {
            n["inputs"] = new JObject()
            {
                ["vae"] = new JArray(vaePath[0], vaePath[1]),
                ["samples"] = new JArray(detachedSeparate, 0),
                ["tile_size"] = 2048,
                ["overlap"] = 256,
                ["temporal_size"] = 64,
                ["temporal_overlap"] = 16
            };
        });
        WGNodeData detachedGuide = new(new JArray(detachedDecode, 0), _generator, WGNodeData.DT_VIDEO, vae.Compat)
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
        if (_generator.CurrentMedia?.Path is not JArray stageOutputPath || stageOutputPath.Count != 2)
        {
            return;
        }

        string newSeparate = _generator.CreateNode(NodeTypes.LTXVSeparateAVLatent, new JObject()
        {
            ["av_latent"] = stageOutputPath
        });

        RetargetVideoDecodeAsTiled(VideoDecodeNodeId, vae?.Path ?? _generator.CurrentVae?.Path, new JArray(newSeparate, 0));
        WorkflowUtils.RetargetInputConnections(
            _generator.Workflow,
            new JArray($"{AudioLatentPath[0]}", AudioLatentPath[1]),
            new JArray(newSeparate, 1),
            connection => connection.NodeId == AudioDecodeNodeId && connection.InputName == "samples");
        _generator.CurrentMedia = CloneMedia(_generator, CurrentOutputMedia);
        AttachSourceAudio(_generator.CurrentMedia);
    }

    public void RetargetAnimationSaves(JArray newImagePath)
    {
        if (newImagePath is null || newImagePath.Count != 2)
        {
            return;
        }

        _ = WorkflowUtils.RetargetInputConnections(
            _generator.Workflow,
            CurrentOutputMedia.Path,
            newImagePath,
            connection =>
            {
                if (!string.Equals(connection.InputName, "images", StringComparison.Ordinal))
                {
                    return false;
                }
                if (_generator.Workflow[connection.NodeId] is not JObject node)
                {
                    return false;
                }
                return $"{node["class_type"]}" == NodeTypes.SwarmSaveAnimationWS;
            });
    }

    private static bool TryFindAudioDecode(JObject workflow, string separateId, out string audioDecodeId, out JArray audioVaeRef)
    {
        audioDecodeId = null;
        audioVaeRef = null;
        foreach (KeyValuePair<string, JToken> entry in workflow)
        {
            if (entry.Value is not JObject node)
            {
                continue;
            }
            if ($"{node["class_type"]}" != NodeTypes.LTXVAudioVAEDecode)
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
                    return false;
                }

                audioDecodeId = entry.Key;
                audioVaeRef = new JArray(foundAudioVaeRef[0], foundAudioVaeRef[1]);
                return true;
            }
        }

        return false;
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
        if (TryGetOriginalAudioLatentPath(out JArray originalAudioLatentPath))
        {
            return new WGNodeData(
                originalAudioLatentPath,
                _generator,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (CurrentOutputMedia?.AttachedAudio?.Path is JArray attachedAudioPath
            && attachedAudioPath.Count == 2
            && CurrentOutputMedia.AttachedAudio.DataType == WGNodeData.DT_LATENT_AUDIO)
        {
            return new WGNodeData(
                new JArray(attachedAudioPath[0], attachedAudioPath[1]),
                _generator,
                CurrentOutputMedia.AttachedAudio.DataType,
                CurrentOutputMedia.AttachedAudio.Compat)
            {
                Width = CurrentOutputMedia.AttachedAudio.Width,
                Height = CurrentOutputMedia.AttachedAudio.Height,
                Frames = CurrentOutputMedia.AttachedAudio.Frames,
                FPS = CurrentOutputMedia.AttachedAudio.FPS
            };
        }

        if (AudioLatentPath is null || AudioLatentPath.Count != 2)
        {
            return null;
        }

        return new WGNodeData(
            new JArray(AudioLatentPath[0], AudioLatentPath[1]),
            _generator,
            WGNodeData.DT_LATENT_AUDIO,
            ResolveAudioCompat());
    }

    private static void RememberOriginalAudioLatent(WorkflowGenerator generator, JArray audioLatentPath)
    {
        if (generator is null
            || audioLatentPath is null
            || audioLatentPath.Count != 2
            || generator.NodeHelpers.ContainsKey(OriginalAudioLatentNodeHelperKey))
        {
            return;
        }

        generator.NodeHelpers[OriginalAudioLatentNodeHelperKey] = audioLatentPath.ToString(Newtonsoft.Json.Formatting.None);
    }

    private bool TryGetOriginalAudioLatentPath(out JArray originalAudioLatentPath)
    {
        originalAudioLatentPath = null;
        if (!_generator.NodeHelpers.TryGetValue(OriginalAudioLatentNodeHelperKey, out string encodedPath)
            || string.IsNullOrWhiteSpace(encodedPath))
        {
            return false;
        }

        try
        {
            if (JToken.Parse(encodedPath) is not JArray parsedPath || parsedPath.Count != 2)
            {
                return false;
            }

            originalAudioLatentPath = new JArray(parsedPath[0], parsedPath[1]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RetargetVideoDecodeAsTiled(string videoDecodeNodeId, JArray vaeRef, JArray latentRef)
    {
        if (string.IsNullOrWhiteSpace(videoDecodeNodeId)
            || vaeRef is null
            || latentRef is null
            || !_generator.Workflow.TryGetValue(videoDecodeNodeId, out JToken decodeToken)
            || decodeToken is not JObject decodeNode)
        {
            return;
        }

        decodeNode["class_type"] = NodeTypes.VAEDecodeTiled;
        decodeNode["inputs"] = new JObject()
        {
            ["vae"] = new JArray(vaeRef[0], vaeRef[1]),
            ["samples"] = new JArray(latentRef[0], latentRef[1]),
            ["tile_size"] = 2048,
            ["overlap"] = 256,
            ["temporal_size"] = 64,
            ["temporal_overlap"] = 16
        };
    }

    private T2IModelCompatClass ResolveVideoCompat()
    {
        return T2IModelClassSorter.CompatLtxv2 ?? _generator.CurrentVae?.Compat ?? CurrentOutputMedia?.Compat ?? _generator.CurrentCompat();
    }

    private T2IModelCompatClass ResolveAudioCompat()
    {
        return _generator.CurrentAudioVae?.Compat ?? ResolveVideoCompat();
    }
}
