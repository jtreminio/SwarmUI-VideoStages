using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class TypedBoundaryTests
{
    public TypedBoundaryTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    private static JObject BuildLtxWorkflow()
    {
        return new JObject
        {
            ["1"] = new JObject
            {
                ["class_type"] = CheckpointLoaderSimpleNode.ClassType,
                ["inputs"] = new JObject { ["ckpt_name"] = "ltxv2.safetensors" }
            },
            ["2"] = new JObject
            {
                ["class_type"] = LTXVAudioVAELoaderNode.ClassType,
                ["inputs"] = new JObject { ["audio_vae_name"] = "audio.safetensors" }
            },
            ["3"] = new JObject
            {
                ["class_type"] = SwarmKSamplerNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["model"] = new JArray("1", 0),
                    ["seed"] = 42,
                    ["steps"] = 20,
                    ["cfg"] = 7.0,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["positive"] = new JArray("99", 0),
                    ["negative"] = new JArray("98", 0),
                    ["latent_image"] = new JArray("97", 0),
                    ["denoise"] = 1.0
                }
            },
            ["4"] = new JObject
            {
                ["class_type"] = LTXVSeparateAVLatentNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["av_latent"] = new JArray("3", 0)
                }
            },
            ["5"] = new JObject
            {
                ["class_type"] = VAEDecodeNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("4", 0),
                    ["vae"] = new JArray("1", 2)
                }
            },
            ["6"] = new JObject
            {
                ["class_type"] = LTXVAudioVAEDecodeNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("4", 1),
                    ["audio_vae"] = new JArray("2", 0)
                }
            },
            ["7"] = new JObject
            {
                ["class_type"] = SwarmSaveAnimationWSNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["images"] = new JArray("5", 0),
                    ["fps"] = 24.0,
                    ["lossless"] = true,
                    ["quality"] = 80,
                    ["method"] = "default",
                    ["format"] = "webp",
                    ["audio"] = new JArray("6", 0)
                }
            }
        };
    }

    private static WorkflowGenerator CreateGenerator(JObject workflow)
    {
        return new WorkflowGenerator
        {
            UserInput = new T2IParamInput(null),
            Workflow = workflow
        };
    }

    private static WorkflowGenerator CreateGeneratorWithCurrentMedia(
        JObject workflow,
        string mediaNodeId = "5",
        string audioVaeNodeId = null)
    {
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = new WGNodeData(
            new JArray(mediaNodeId, 0), generator, WGNodeData.DT_VIDEO, null)
        {
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24
        };
        if (audioVaeNodeId is not null)
        {
            generator.CurrentAudioVae = new WGNodeData(
                new JArray(audioVaeNodeId, 0), generator, WGNodeData.DT_AUDIOVAE, null);
        }
        return generator;
    }

    [Fact]
    public void MediaRef_FromWGNodeData_ResolvesTypedOutput()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData data = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1280,
            Height = 720
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(data, bridge);

        Assert.NotNull(mediaRef);
        Assert.Equal("5", mediaRef.Output.Node.Id);
        Assert.Equal(0, mediaRef.Output.SlotIndex);
        Assert.Equal(WGNodeData.DT_VIDEO, mediaRef.DataType);
        Assert.Equal(1280, mediaRef.Width);
        Assert.Equal(720, mediaRef.Height);
    }

    [Fact]
    public void MediaRef_FromWGNodeData_WithAttachedAudio_ResolvesRecursively()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData audio = new(new JArray("6", 0), g, WGNodeData.DT_AUDIO, null);
        WGNodeData data = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            AttachedAudio = audio
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(data, bridge);

        Assert.NotNull(mediaRef);
        Assert.NotNull(mediaRef.AttachedAudio);
        Assert.Equal("6", mediaRef.AttachedAudio.Output.Node.Id);
        Assert.Equal(WGNodeData.DT_AUDIO, mediaRef.AttachedAudio.DataType);
    }

    [Fact]
    public void MediaRef_FromWGNodeData_NullPath_ReturnsNull()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        MediaRef result = MediaRef.FromWGNodeData(null, bridge);
        Assert.Null(result);
    }

    [Fact]
    public void MediaRef_FromWGNodeData_UnresolvablePath_ReturnsNull()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData data = new(new JArray("nonexistent", 0), g, WGNodeData.DT_VIDEO, null);

        MediaRef result = MediaRef.FromWGNodeData(data, bridge);
        Assert.Null(result);
    }

    [Fact]
    public void MediaRef_ToWGNodeData_ProducesCorrectPath()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData original = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(original, bridge);
        WGNodeData roundTripped = mediaRef.ToWGNodeData(g);

        Assert.Equal("5", $"{roundTripped.Path[0]}");
        Assert.Equal(0, roundTripped.Path[1].Value<int>());
        Assert.Equal(WGNodeData.DT_VIDEO, roundTripped.DataType);
        Assert.Equal(1280, roundTripped.Width);
        Assert.Equal(720, roundTripped.Height);
        Assert.Equal(97, roundTripped.Frames);
        Assert.Equal(24, roundTripped.FPS);
    }

    [Fact]
    public void MediaRef_ToWGNodeData_WithAttachedAudio_Recursive()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData audio = new(new JArray("6", 0), g, WGNodeData.DT_AUDIO, null);
        WGNodeData original = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            AttachedAudio = audio
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(original, bridge);
        WGNodeData roundTripped = mediaRef.ToWGNodeData(g);

        Assert.NotNull(roundTripped.AttachedAudio);
        Assert.Equal("6", $"{roundTripped.AttachedAudio.Path[0]}");
        Assert.Equal(WGNodeData.DT_AUDIO, roundTripped.AttachedAudio.DataType);
    }

    [Fact]
    public void MediaRef_RoundTrip_WGNodeData_MediaRef_WGNodeData()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData original = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1920,
            Height = 1080,
            Frames = 49,
            FPS = 30
        };

        MediaRef typed = MediaRef.FromWGNodeData(original, bridge);
        WGNodeData back = typed.ToWGNodeData(g);

        Assert.Equal($"{original.Path[0]}", $"{back.Path[0]}");
        Assert.Equal(original.Path[1].Value<int>(), back.Path[1].Value<int>());
        Assert.Equal(original.DataType, back.DataType);
        Assert.Equal(original.Width, back.Width);
        Assert.Equal(original.Height, back.Height);
        Assert.Equal(original.Frames, back.Frames);
        Assert.Equal(original.FPS, back.FPS);
    }

    [Fact]
    public void MediaRef_Clone_PreservesAllFields()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData audio = new(new JArray("6", 0), g, WGNodeData.DT_AUDIO, null);
        WGNodeData data = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24,
            AttachedAudio = audio
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(data, bridge);
        MediaRef cloned = mediaRef.Clone();

        Assert.Same(mediaRef.Output, cloned.Output);
        Assert.Equal(mediaRef.Width, cloned.Width);
        Assert.Equal(mediaRef.Height, cloned.Height);
        Assert.Equal(mediaRef.Frames, cloned.Frames);
        Assert.Equal(mediaRef.FPS, cloned.FPS);
        Assert.NotNull(cloned.AttachedAudio);
        Assert.Same(mediaRef.AttachedAudio.Output, cloned.AttachedAudio.Output);
    }

    [Fact]
    public void TryCapture_NoDecodeUpstream_ReturnsNull()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = SwarmKSamplerNode.ClassType,
                ["inputs"] = new JObject { ["seed"] = 42 }
            }
        };
        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow, "1");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        Assert.Null(capture);
    }

    [Fact]
    public void TryCapture_NoSeparateNode_ReturnsNull()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = SwarmKSamplerNode.ClassType,
                ["inputs"] = new JObject { ["seed"] = 42 }
            },
            ["2"] = new JObject
            {
                ["class_type"] = VAEDecodeNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("1", 0),
                    ["vae"] = new JArray("99", 0)
                }
            }
        };
        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow, "2");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        Assert.Null(capture);
    }

    [Fact]
    public void TryCapture_NoAudioDecode_FallsBackToCurrentAudioVae()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = CheckpointLoaderSimpleNode.ClassType,
                ["inputs"] = new JObject { ["ckpt_name"] = "model.safetensors" }
            },
            ["3"] = new JObject
            {
                ["class_type"] = SwarmKSamplerNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["model"] = new JArray("1", 0),
                    ["seed"] = 42,
                    ["steps"] = 20,
                    ["cfg"] = 7.0,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["positive"] = new JArray("99", 0),
                    ["negative"] = new JArray("98", 0),
                    ["latent_image"] = new JArray("97", 0),
                    ["denoise"] = 1.0
                }
            },
            ["4"] = new JObject
            {
                ["class_type"] = LTXVSeparateAVLatentNode.ClassType,
                ["inputs"] = new JObject { ["av_latent"] = new JArray("3", 0) }
            },
            ["5"] = new JObject
            {
                ["class_type"] = VAEDecodeNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("4", 0),
                    ["vae"] = new JArray("1", 2)
                }
            },
            ["2"] = new JObject
            {
                ["class_type"] = LTXVAudioVAELoaderNode.ClassType,
                ["inputs"] = new JObject { ["audio_vae_name"] = "audio.safetensors" }
            }
        };
        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(
            workflow, audioVaeNodeId: "2");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        Assert.NotNull(capture);
    }

    [Fact]
    public void TryCapture_WithPostDecodeWrappers_DetectsCorrectly()
    {
        JObject workflow = BuildLtxWorkflow();
        workflow["8"] = new JObject
        {
            ["class_type"] = ImageScaleNode.ClassType,
            ["inputs"] = new JObject
            {
                ["image"] = new JArray("5", 0),
                ["upscale_method"] = "lanczos",
                ["width"] = 640,
                ["height"] = 360,
                ["crop"] = "center"
            }
        };
        ((JObject)workflow["7"]!)["inputs"]!["images"] = new JArray("8", 0);
        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow, "8");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        Assert.NotNull(capture);
        Assert.True(capture.HasPostDecodeWrappers);
        Assert.Equal("5", capture.VideoDecodeNodeId);
    }

    [Fact]
    public void TryCapture_TerminalSaveNodeOutput_ReturnsNull()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow, "7");
        Assert.Null(LtxPostVideoChain.TryCapture(generator));
    }

    [Fact]
    public void Splice_RetargetsDecodeToNewSeparate()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow);
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        var newKSampler = bridge.AddNode(new KSamplerNode());
        generator.CurrentMedia = new WGNodeData(
            new JArray(newKSampler.Id, 0), generator, WGNodeData.DT_LATENT_AUDIOVIDEO, null);
        WGNodeData vae = new(new JArray("1", 2), generator, WGNodeData.DT_VAE, null);

        capture.SpliceCurrentOutput(vae);

        JObject decodeNode = workflow["5"] as JObject;
        JArray samplesRef = decodeNode?["inputs"]?["samples"] as JArray;
        Assert.NotNull(samplesRef);

        string newSeparateId = $"{samplesRef[0]}";
        Assert.NotEqual("4", newSeparateId);

        JObject newSeparateNode = workflow[newSeparateId] as JObject;
        Assert.NotNull(newSeparateNode);
        Assert.Equal(LTXVSeparateAVLatentNode.ClassType, $"{newSeparateNode["class_type"]}");
    }

    [Fact]
    public void Splice_RetargetsAudioDecodeToNewSeparate()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow);
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        var newKSampler = bridge.AddNode(new KSamplerNode());
        generator.CurrentMedia = new WGNodeData(
            new JArray(newKSampler.Id, 0), generator, WGNodeData.DT_LATENT_AUDIOVIDEO, null);
        WGNodeData vae = new(new JArray("1", 2), generator, WGNodeData.DT_VAE, null);

        capture.SpliceCurrentOutput(vae);

        JObject audioDecodeNode = workflow["6"] as JObject;
        JArray audioSamplesRef = audioDecodeNode?["inputs"]?["samples"] as JArray;
        Assert.NotNull(audioSamplesRef);

        string newSeparateId = $"{audioSamplesRef[0]}";
        Assert.NotEqual("4", newSeparateId);
        Assert.Equal(1, audioSamplesRef[1].Value<int>());
    }

    [Fact]
    public void Splice_ReturnsClonedMediaRef()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow);
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        var newKSampler = bridge.AddNode(new KSamplerNode());
        generator.CurrentMedia = new WGNodeData(
            new JArray(newKSampler.Id, 0), generator, WGNodeData.DT_LATENT_AUDIOVIDEO, null);
        WGNodeData vae = new(new JArray("1", 2), generator, WGNodeData.DT_VAE, null);

        capture.SpliceCurrentOutput(vae);
        WGNodeData result = generator.CurrentMedia;

        Assert.NotNull(result);
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.Equal(97, result.Frames);
        Assert.Equal(24, result.FPS);
    }

    [Fact]
    public void AttachAudio_ReusesExistingDecodeNode()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO };

        INodeOutput audioVaeOutput = bridge.ResolvePath(new JArray("2", 0));
        MediaRef audioVae = new() { Output = audioVaeOutput, DataType = WGNodeData.DT_AUDIOVAE };

        LtxPostChainRebuilder.AttachDecodedLtxAudio(bridge, media, audioVae);

        Assert.NotNull(media.AttachedAudio);
        Assert.Equal(WGNodeData.DT_AUDIO, media.AttachedAudio.DataType);
        Assert.Equal("6", media.AttachedAudio.Output.Node.Id);
        Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());
    }

    [Fact]
    public void AttachAudio_CreatesDecodeNodeWhenMissing()
    {
        JObject workflow = BuildLtxWorkflow();
        workflow.Remove("6");
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        MediaRef media = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO,
        };
        MediaRef audioVae = new()
        {
            Output = bridge.ResolvePath(new JArray("2", 0)),
            DataType = WGNodeData.DT_AUDIOVAE,
        };

        LtxPostChainRebuilder.AttachDecodedLtxAudio(bridge, media, audioVae);

        LTXVAudioVAEDecodeNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());
        Assert.Same(audioDecode.Audio, media.AttachedAudio.Output);
        Assert.Same(audioDecode.AudioVae.Connection, audioVae.Output);
    }

    [Fact]
    public void AttachAudio_DoesNotReuseADecoderWithAnotherAudioVae()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode requestedAudioVae = bridge.AddStub("UnitTest_AudioVae", "alternate-audio-vae")
            .WithOutputs(WGNodeData.DT_AUDIOVAE);
        MediaRef media = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO,
        };
        MediaRef audioVae = new()
        {
            Output = requestedAudioVae.GetOutput(0),
            DataType = WGNodeData.DT_AUDIOVAE,
        };

        LtxPostChainRebuilder.AttachDecodedLtxAudio(bridge, media, audioVae);

        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>().Count);
        LTXVAudioVAEDecodeNode selected = Assert.IsType<LTXVAudioVAEDecodeNode>(
            media.AttachedAudio.Output.Node);
        Assert.Same(requestedAudioVae.GetOutput(0), selected.AudioVae.Connection);
    }

    [Fact]
    public void AttachAudio_ReplacesAnUnrelatedDecodedAttachment()
    {
        JObject workflow = BuildLtxWorkflow();
        workflow.Remove("6");
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode upload = bridge.AddStub("UnitTest_UploadAudio", "upload")
            .WithOutputs(WGNodeData.DT_AUDIO);
        MediaRef media = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO,
            AttachedAudio = new MediaRef
            {
                Output = upload.GetOutput(0),
                DataType = WGNodeData.DT_AUDIO,
            },
        };
        MediaRef audioVae = new()
        {
            Output = bridge.ResolvePath(new JArray("2", 0)),
            DataType = WGNodeData.DT_AUDIOVAE,
        };

        LtxPostChainRebuilder.AttachDecodedLtxAudio(bridge, media, audioVae);

        LTXVAudioVAEDecodeNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());
        Assert.Same(audioDecode.Audio, media.AttachedAudio.Output);
        Assert.NotSame(upload.GetOutput(0), media.AttachedAudio.Output);
    }

    [Fact]
    public void AttachAudio_NoDecodeUpstream_DoesNothing()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = SwarmKSamplerNode.ClassType,
                ["inputs"] = new JObject { ["seed"] = 42 }
            }
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput output = bridge.ResolvePath(new JArray("1", 0));
        MediaRef media = new() { Output = output, DataType = WGNodeData.DT_VIDEO };
        MediaRef audioVae = new() { Output = output, DataType = WGNodeData.DT_AUDIOVAE };

        LtxPostChainRebuilder.AttachDecodedLtxAudio(bridge, media, audioVae);

        Assert.Null(media.AttachedAudio);
    }

    [Fact]
    public void FullStage_TryCapture_Then_Splice_ProducesValidWorkflow()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator generator = CreateGeneratorWithCurrentMedia(workflow);
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        Assert.NotNull(capture);

        workflow["50"] = new JObject
        {
            ["class_type"] = SwarmKSamplerNode.ClassType,
            ["inputs"] = new JObject
            {
                ["seed"] = 123,
                ["steps"] = 30,
                ["cfg"] = 3.0,
                ["sampler_name"] = "euler",
                ["scheduler"] = "normal",
                ["model"] = new JArray("1", 0),
                ["positive"] = new JArray("99", 0),
                ["negative"] = new JArray("98", 0),
                ["latent_image"] = new JArray("97", 0),
                ["denoise"] = 1.0
            }
        };
        generator.CurrentMedia = new WGNodeData(
            new JArray("50", 0), generator, WGNodeData.DT_LATENT_AUDIOVIDEO, null);
        WGNodeData vae = new(new JArray("1", 2), generator, WGNodeData.DT_VAE, null);

        capture.SpliceCurrentOutput(vae);

        Assert.Equal("5", $"{generator.CurrentMedia.Path[0]}");
        JArray decSamples = workflow["5"]?["inputs"]?["samples"] as JArray;
        Assert.NotNull(decSamples);
        string newSepId = $"{decSamples[0]}";
        Assert.NotEqual("4", newSepId);

        JObject newSep = workflow[newSepId] as JObject;
        JArray avLatent = newSep?["inputs"]?["av_latent"] as JArray;
        Assert.Equal("50", $"{avLatent[0]}");
    }

    private static JObject BuildAudioInjectionWorkflow()
    {
        return new JObject
        {
            ["1"] = new JObject
            {
                ["class_type"] = SwarmKSamplerNode.ClassType,
                ["inputs"] = new JObject { ["seed"] = 42 }
            },
            ["2"] = new JObject
            {
                ["class_type"] = LTXVEmptyLatentAudioNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["frames_number"] = 97,
                    ["frame_rate"] = 24,
                    ["batch_size"] = 1,
                    ["audio_vae"] = new JArray("10", 0)
                }
            },
            ["3"] = new JObject
            {
                ["class_type"] = LTXVConcatAVLatentNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["video_latent"] = new JArray("1", 0),
                    ["audio_latent"] = new JArray("2", 0)
                }
            }
        };
    }

    [Fact]
    public void AudioInjection_FindsConcatWithEmptyAudioSource()
    {
        JObject workflow = BuildAudioInjectionWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<(LTXVConcatAVLatentNode Concat, LTXVEmptyLatentAudioNode Empty)> found = [];
        foreach (LTXVConcatAVLatentNode concat in bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>())
        {
            if (concat.AudioLatent.Connection?.Node is LTXVEmptyLatentAudioNode emptyAudio)
                found.Add((concat, emptyAudio));
        }

        Assert.Single(found);
        Assert.Equal("3", found[0].Concat.Id);
        Assert.Equal("2", found[0].Empty.Id);
    }

    [Fact]
    public void AudioInjection_IgnoresNonEmptyAudioSources()
    {
        JObject workflow = BuildAudioInjectionWorkflow();
        ((JObject)workflow["3"]!)["inputs"]!["audio_latent"] = new JArray("1", 0);

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<LTXVConcatAVLatentNode> found = [];
        foreach (LTXVConcatAVLatentNode concat in bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>())
        {
            if (concat.AudioLatent.Connection?.Node is LTXVEmptyLatentAudioNode)
                found.Add(concat);
        }

        Assert.Empty(found);
    }

    [Fact]
    public void AudioInjection_FindsMultipleConcatNodes()
    {
        JObject workflow = BuildAudioInjectionWorkflow();
        workflow["12"] = new JObject
        {
            ["class_type"] = LTXVEmptyLatentAudioNode.ClassType,
            ["inputs"] = new JObject
            {
                ["frames_number"] = 49,
                ["frame_rate"] = 30,
                ["batch_size"] = 1,
                ["audio_vae"] = new JArray("10", 0)
            }
        };
        workflow["13"] = new JObject
        {
            ["class_type"] = LTXVConcatAVLatentNode.ClassType,
            ["inputs"] = new JObject
            {
                ["video_latent"] = new JArray("1", 0),
                ["audio_latent"] = new JArray("12", 0)
            }
        };

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<string> emptyIds = [];
        foreach (LTXVConcatAVLatentNode concat in bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>())
        {
            if (concat.AudioLatent.Connection?.Node is LTXVEmptyLatentAudioNode emptyAudio)
                emptyIds.Add(emptyAudio.Id);
        }

        Assert.Equal(2, emptyIds.Count);
        Assert.Contains("2", emptyIds);
        Assert.Contains("12", emptyIds);
    }

    [Fact]
    public void AudioInjection_JObjectStillHasMutableAudioLatentRef()
    {
        JObject workflow = BuildAudioInjectionWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVConcatAVLatentNode concat = bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().First();
        JArray audioLatent = workflow[concat.Id]?["inputs"]?["audio_latent"] as JArray;

        Assert.NotNull(audioLatent);
        Assert.Equal("2", $"{audioLatent[0]}");

        audioLatent[0] = "99";
        audioLatent[1] = 0;

        JArray afterMutation = workflow["3"]?["inputs"]?["audio_latent"] as JArray;
        Assert.Equal("99", $"{afterMutation[0]}");
    }

    [Fact]
    public void SyncLastId_AfterBridgeOps_CoversBridgeIds()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        g.LastID = 100;

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.AddNode(new KSamplerNode());
        bridge.AddNode(new KSamplerNode());

        BridgeSync.SyncLastId(g);

        foreach (JProperty prop in workflow.Properties())
        {
            if (int.TryParse(prop.Name, out int n))
                Assert.True(g.LastID > n, $"g.LastID ({g.LastID}) should be > {n}");
        }
    }

}
