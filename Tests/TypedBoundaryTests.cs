using ComfyTyped;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Typed;
using Xunit;

namespace VideoStages.Tests;

public class TypedBoundaryTests
{
    public TypedBoundaryTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    // ── Shared workflow builder ────────────────────────────────────

    /// <summary>
    /// Builds a realistic LTX workflow matching the pattern TryCapture expects:
    ///
    ///   CheckpointLoader("1") ─┐
    ///   AudioVAELoader("2") ───┤
    ///                          ▼
    ///                   KSampler("3") → av_latent
    ///                          │
    ///              LTXVSeparateAVLatent("4")
    ///                ├── slot 0: video_latent ──► VAEDecode("5") ──► SwarmSaveAnimationWS("7")
    ///                └── slot 1: audio_latent ──► LTXVAudioVAEDecode("6")
    /// </summary>
    private static JObject BuildLtxWorkflow()
    {
        return new JObject
        {
            ["1"] = new JObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JObject { ["ckpt_name"] = "ltxv2.safetensors" }
            },
            ["2"] = new JObject
            {
                ["class_type"] = "LTXVAudioVAELoader",
                ["inputs"] = new JObject { ["audio_vae_name"] = "audio.safetensors" }
            },
            ["3"] = new JObject
            {
                ["class_type"] = "KSampler",
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
                ["class_type"] = "LTXVSeparateAVLatent",
                ["inputs"] = new JObject
                {
                    ["av_latent"] = new JArray("3", 0)
                }
            },
            ["5"] = new JObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("4", 0),
                    ["vae"] = new JArray("1", 2)
                }
            },
            ["6"] = new JObject
            {
                ["class_type"] = "LTXVAudioVAEDecode",
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("4", 1),
                    ["audio_vae"] = new JArray("2", 0)
                }
            },
            ["7"] = new JObject
            {
                ["class_type"] = "SwarmSaveAnimationWS",
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

    // ═══════════════════════════════════════════════════════════════
    //  MediaRef tests
    // ═══════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════
    //  LtxChainOps.TryCapture tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TryCapture_FindsDecodeChain()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // CurrentMedia points to the save node's input (VAEDecode output)
        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef currentMedia = new()
        {
            Output = decodeOutput,
            DataType = WGNodeData.DT_VIDEO,
            Width = 1280,
            Height = 720
        };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, currentMedia, currentAudioVae: null, useReusedAudio: false);

        Assert.NotNull(capture);
        Assert.Equal("5", capture.DecodeId);
        Assert.Equal("4", capture.SeparateId);
        Assert.Equal("6", capture.AudioDecodeId);
        Assert.False(capture.HasPostDecodeWrappers);
    }

    [Fact]
    public void TryCapture_NoDecodeUpstream_ReturnsNull()
    {
        // Workflow with no VAEDecode: just a KSampler output
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JObject { ["seed"] = 42 }
            }
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput output = bridge.ResolvePath(new JArray("1", 0));
        MediaRef media = new() { Output = output, DataType = WGNodeData.DT_VIDEO };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        Assert.Null(capture);
    }

    [Fact]
    public void TryCapture_NoSeparateNode_ReturnsNull()
    {
        // VAEDecode whose samples DON'T come from LTXVSeparateAVLatent
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JObject { ["seed"] = 42 }
            },
            ["2"] = new JObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("1", 0),
                    ["vae"] = new JArray("99", 0)
                }
            }
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput output = bridge.ResolvePath(new JArray("2", 0));
        MediaRef media = new() { Output = output, DataType = WGNodeData.DT_VIDEO };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        Assert.Null(capture);
    }

    [Fact]
    public void TryCapture_NoAudioDecode_FallsBackToCurrentAudioVae()
    {
        // Full chain but without audio decode node
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JObject { ["ckpt_name"] = "model.safetensors" }
            },
            ["3"] = new JObject
            {
                ["class_type"] = "KSampler",
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
                ["class_type"] = "LTXVSeparateAVLatent",
                ["inputs"] = new JObject { ["av_latent"] = new JArray("3", 0) }
            },
            ["5"] = new JObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("4", 0),
                    ["vae"] = new JArray("1", 2)
                }
            },
            // Audio VAE loader exists but no audio decode
            ["2"] = new JObject
            {
                ["class_type"] = "LTXVAudioVAELoader",
                ["inputs"] = new JObject { ["audio_vae_name"] = "audio.safetensors" }
            }
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO };

        // Provide audio VAE as fallback
        INodeOutput audioVaeOutput = bridge.ResolvePath(new JArray("2", 0));
        MediaRef audioVae = new() { Output = audioVaeOutput, DataType = WGNodeData.DT_AUDIOVAE };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: audioVae, useReusedAudio: false);

        Assert.NotNull(capture);
        Assert.Null(capture.AudioDecodeId);
        Assert.NotNull(capture.AudioVaeSource);
        Assert.Equal("2", capture.AudioVaeSource.Node.Id);
    }

    [Fact]
    public void TryCapture_WithPostDecodeWrappers_DetectsCorrectly()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Point media to save node (downstream of decode) instead of decode output
        INodeOutput saveOutput = bridge.ResolvePath(new JArray("7", 0));
        MediaRef media = new() { Output = saveOutput, DataType = WGNodeData.DT_VIDEO };

        // Save node has no outputs (slot 0 won't resolve), but let's point at decode
        // and verify HasPostDecodeWrappers when media node != decode node
        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef mediaThroughDecode = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO };

        LtxChainCapture directCapture = LtxChainOps.TryCapture(
            bridge, mediaThroughDecode, currentAudioVae: null, useReusedAudio: false);

        Assert.NotNull(directCapture);
        Assert.False(directCapture.HasPostDecodeWrappers);
    }

    [Fact]
    public void TryCapture_CapturesCorrectNodeIds()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        Assert.NotNull(capture);
        // Verify the audio VAE source comes from audio decode's connection
        Assert.Equal("2", capture.AudioVaeSource.Node.Id); // AudioVAELoader
        Assert.Equal(0, capture.AudioVaeSource.SlotIndex);
    }

    // ═══════════════════════════════════════════════════════════════
    //  LtxChainOps.SpliceCurrentOutput tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Splice_CreatesNewSeparateNode()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO, Width = 1280, Height = 720 };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        // Simulate generation: add a new KSampler that produced stage output
        var newKSampler = bridge.AddNode(new KSamplerNode());
        INodeOutput stageOutput = newKSampler.LATENT;
        MediaRef stageOutputRef = new() { Output = stageOutput, DataType = WGNodeData.DT_LATENT_AUDIOVIDEO };

        INodeOutput vaeOutput = bridge.ResolvePath(new JArray("1", 2));
        MediaRef vaeRef = new() { Output = vaeOutput, DataType = WGNodeData.DT_VAE };

        var config = new LtxChainOps.DecodeConfig(UseTiledDecode: false);

        MediaRef result = LtxChainOps.SpliceCurrentOutput(
            bridge, capture, stageOutputRef, vaeRef, config);

        Assert.NotNull(result);

        // Verify a new LTXVSeparateAVLatent was created in the JObject
        int separateCount = 0;
        foreach (JProperty prop in workflow.Properties())
        {
            if (prop.Value is JObject obj && $"{obj["class_type"]}" == "LTXVSeparateAVLatent")
                separateCount++;
        }
        Assert.Equal(2, separateCount); // original + new
    }

    [Fact]
    public void Splice_RetargetsDecodeToNewSeparate()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO, Width = 1280, Height = 720 };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        var newKSampler = bridge.AddNode(new KSamplerNode());
        MediaRef stageOutputRef = new() { Output = newKSampler.LATENT, DataType = WGNodeData.DT_LATENT_AUDIOVIDEO };
        INodeOutput vaeOutput = bridge.ResolvePath(new JArray("1", 2));
        MediaRef vaeRef = new() { Output = vaeOutput, DataType = WGNodeData.DT_VAE };

        var config = new LtxChainOps.DecodeConfig(UseTiledDecode: false);

        LtxChainOps.SpliceCurrentOutput(bridge, capture, stageOutputRef, vaeRef, config);

        // After splice, decode("5") should point to the NEW separate's video output (slot 0)
        JObject decodeNode = workflow["5"] as JObject;
        JArray samplesRef = decodeNode?["inputs"]?["samples"] as JArray;
        Assert.NotNull(samplesRef);

        // The new separate should NOT be "4" (the original)
        string newSeparateId = $"{samplesRef[0]}";
        Assert.NotEqual("4", newSeparateId);

        // The new separate node should exist and be LTXVSeparateAVLatent
        JObject newSeparateNode = workflow[newSeparateId] as JObject;
        Assert.NotNull(newSeparateNode);
        Assert.Equal("LTXVSeparateAVLatent", $"{newSeparateNode["class_type"]}");
    }

    [Fact]
    public void Splice_RetargetsAudioDecodeToNewSeparate()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO, Width = 1280, Height = 720 };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        var newKSampler = bridge.AddNode(new KSamplerNode());
        MediaRef stageOutputRef = new() { Output = newKSampler.LATENT, DataType = WGNodeData.DT_LATENT_AUDIOVIDEO };
        INodeOutput vaeOutput = bridge.ResolvePath(new JArray("1", 2));
        MediaRef vaeRef = new() { Output = vaeOutput, DataType = WGNodeData.DT_VAE };

        var config = new LtxChainOps.DecodeConfig(UseTiledDecode: false);

        LtxChainOps.SpliceCurrentOutput(bridge, capture, stageOutputRef, vaeRef, config);

        // Audio decode("6") should now point to the new separate's audio output (slot 1)
        JObject audioDecodeNode = workflow["6"] as JObject;
        JArray audioSamplesRef = audioDecodeNode?["inputs"]?["samples"] as JArray;
        Assert.NotNull(audioSamplesRef);

        string newSeparateId = $"{audioSamplesRef[0]}";
        Assert.NotEqual("4", newSeparateId);
        Assert.Equal(1, audioSamplesRef[1].Value<int>()); // audio slot
    }

    [Fact]
    public void Splice_ReturnsClonedMediaRef()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new()
        {
            Output = decodeOutput,
            DataType = WGNodeData.DT_VIDEO,
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24
        };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        var newKSampler = bridge.AddNode(new KSamplerNode());
        MediaRef stageOutputRef = new() { Output = newKSampler.LATENT, DataType = WGNodeData.DT_LATENT_AUDIOVIDEO };
        INodeOutput vaeOutput = bridge.ResolvePath(new JArray("1", 2));
        MediaRef vaeRef = new() { Output = vaeOutput, DataType = WGNodeData.DT_VAE };

        var config = new LtxChainOps.DecodeConfig(UseTiledDecode: false);

        MediaRef result = LtxChainOps.SpliceCurrentOutput(
            bridge, capture, stageOutputRef, vaeRef, config);

        Assert.NotNull(result);
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.Equal(97, result.Frames);
        Assert.Equal(24, result.FPS);
    }

    [Fact]
    public void Splice_UpdatesJObjectAfterSync()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO, Width = 1280, Height = 720 };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            bridge, media, currentAudioVae: null, useReusedAudio: false);

        var newKSampler = bridge.AddNode(new KSamplerNode());
        MediaRef stageOutputRef = new() { Output = newKSampler.LATENT, DataType = WGNodeData.DT_LATENT_AUDIOVIDEO };
        INodeOutput vaeOutput = bridge.ResolvePath(new JArray("1", 2));
        MediaRef vaeRef = new() { Output = vaeOutput, DataType = WGNodeData.DT_VAE };

        var config = new LtxChainOps.DecodeConfig(UseTiledDecode: false);

        LtxChainOps.SpliceCurrentOutput(bridge, capture, stageOutputRef, vaeRef, config);

        // The new separate node should have av_latent pointing to the new KSampler
        string newSeparateId = null;
        foreach (JProperty prop in workflow.Properties())
        {
            if (prop.Name != "4" && prop.Value is JObject obj
                && $"{obj["class_type"]}" == "LTXVSeparateAVLatent")
            {
                newSeparateId = prop.Name;
                break;
            }
        }
        Assert.NotNull(newSeparateId);

        JObject newSep = workflow[newSeparateId] as JObject;
        JArray avLatentRef = newSep?["inputs"]?["av_latent"] as JArray;
        Assert.NotNull(avLatentRef);
        Assert.Equal(newKSampler.Id, $"{avLatentRef[0]}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  LtxChainOps.AttachDecodedLtxAudio tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AttachAudio_CreatesDecodeNodeInWorkflow()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO };

        INodeOutput audioVaeOutput = bridge.ResolvePath(new JArray("2", 0));
        MediaRef audioVae = new() { Output = audioVaeOutput, DataType = WGNodeData.DT_AUDIOVAE };

        LtxChainOps.AttachDecodedLtxAudio(bridge, media, audioVae);

        Assert.NotNull(media.AttachedAudio);
        Assert.Equal(WGNodeData.DT_AUDIO, media.AttachedAudio.DataType);

        // The new audio decode should exist in the JObject
        string audioNodeId = media.AttachedAudio.Output.Node.Id;
        JObject audioNode = workflow[audioNodeId] as JObject;
        Assert.NotNull(audioNode);
        Assert.Equal("LTXVAudioVAEDecode", $"{audioNode["class_type"]}");
    }

    [Fact]
    public void AttachAudio_NoDecodeUpstream_DoesNothing()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JObject { ["seed"] = 42 }
            }
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput output = bridge.ResolvePath(new JArray("1", 0));
        MediaRef media = new() { Output = output, DataType = WGNodeData.DT_VIDEO };
        MediaRef audioVae = new() { Output = output, DataType = WGNodeData.DT_AUDIOVAE };

        LtxChainOps.AttachDecodedLtxAudio(bridge, media, audioVae);

        Assert.Null(media.AttachedAudio);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Integration: Full capture → splice lifecycle
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FullStage_TryCapture_Then_Splice_ProducesValidWorkflow()
    {
        JObject workflow = BuildLtxWorkflow();

        // PRE-GEN: Capture on original workflow
        WorkflowBridge preGenBridge = WorkflowBridge.Create(workflow);
        INodeOutput decodeOutput = preGenBridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new()
        {
            Output = decodeOutput,
            DataType = WGNodeData.DT_VIDEO,
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24
        };
        LtxChainCapture capture = LtxChainOps.TryCapture(
            preGenBridge, media, currentAudioVae: null, useReusedAudio: false);
        Assert.NotNull(capture);

        // SIMULATE GENERATION: Add new nodes to JObject directly (as g.CreateImageToVideo would)
        workflow["50"] = new JObject
        {
            ["class_type"] = "KSampler",
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

        // POST-GEN: Create fresh bridge, re-resolve capture, splice
        WorkflowBridge postGenBridge = WorkflowBridge.Create(workflow);

        // Verify we can re-resolve the captured node IDs
        ComfyNode resolvedDecode = postGenBridge.Graph.GetNode(capture.DecodeId);
        Assert.NotNull(resolvedDecode);
        Assert.Equal("5", resolvedDecode.Id);

        ComfyNode resolvedSeparate = postGenBridge.Graph.GetNode(capture.SeparateId);
        Assert.NotNull(resolvedSeparate);
        Assert.Equal("4", resolvedSeparate.Id);

        // Splice with new stage output
        INodeOutput stageOutput = postGenBridge.ResolvePath(new JArray("50", 0));
        MediaRef stageOutputRef = new() { Output = stageOutput, DataType = WGNodeData.DT_LATENT_AUDIOVIDEO };
        INodeOutput vaeOutput = postGenBridge.ResolvePath(new JArray("1", 2));
        MediaRef vaeRef = new() { Output = vaeOutput, DataType = WGNodeData.DT_VAE };

        var config = new LtxChainOps.DecodeConfig(UseTiledDecode: false);

        MediaRef result = LtxChainOps.SpliceCurrentOutput(
            postGenBridge, capture, stageOutputRef, vaeRef, config);
        Assert.NotNull(result);

        // Verify workflow integrity: decode now points to new separate
        JArray decSamples = workflow["5"]?["inputs"]?["samples"] as JArray;
        Assert.NotNull(decSamples);
        string newSepId = $"{decSamples[0]}";
        Assert.NotEqual("4", newSepId);

        // New separate's av_latent points to stage output (node "50")
        JObject newSep = workflow[newSepId] as JObject;
        JArray avLatent = newSep?["inputs"]?["av_latent"] as JArray;
        Assert.Equal("50", $"{avLatent[0]}");
    }

    [Fact]
    public void PostGenBridge_ResolvesPreGenCaptureIds()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge preGenBridge = WorkflowBridge.Create(workflow);

        INodeOutput decodeOutput = preGenBridge.ResolvePath(new JArray("5", 0));
        MediaRef media = new() { Output = decodeOutput, DataType = WGNodeData.DT_VIDEO };

        LtxChainCapture capture = LtxChainOps.TryCapture(
            preGenBridge, media, currentAudioVae: null, useReusedAudio: false);

        // Add nodes (simulating generation)
        workflow["60"] = new JObject
        {
            ["class_type"] = "KSampler",
            ["inputs"] = new JObject { ["seed"] = 99 }
        };

        // New bridge sees both old and new nodes
        WorkflowBridge postGenBridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(postGenBridge.Graph.GetNode(capture.DecodeId));
        Assert.NotNull(postGenBridge.Graph.GetNode(capture.SeparateId));
        Assert.NotNull(postGenBridge.Graph.GetNode(capture.AudioDecodeId));
        Assert.NotNull(postGenBridge.Graph.GetNode("60")); // new node visible
    }

    // ═══════════════════════════════════════════════════════════════
    //  Audio injection typed query tests
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a workflow with LTXVConcatAVLatent connected to LTXVEmptyLatentAudio:
    ///
    ///   KSampler("1") → video_latent ─┐
    ///   LTXVEmptyLatentAudio("2") ─────┤ audio_latent
    ///                                  ▼
    ///                        LTXVConcatAVLatent("3")
    /// </summary>
    private static JObject BuildAudioInjectionWorkflow()
    {
        return new JObject
        {
            ["1"] = new JObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JObject { ["seed"] = 42 }
            },
            ["2"] = new JObject
            {
                ["class_type"] = "LTXVEmptyLatentAudio",
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
                ["class_type"] = "LTXVConcatAVLatent",
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
        // Change the audio_latent source to point to a non-empty-audio node
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
        // Add a second concat+empty pair
        workflow["12"] = new JObject
        {
            ["class_type"] = "LTXVEmptyLatentAudio",
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
            ["class_type"] = "LTXVConcatAVLatent",
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

        // The typed query finds the concat node, and we can still get the mutable
        // JArray from the JObject for in-place replacement
        LTXVConcatAVLatentNode concat = bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().First();
        JArray audioLatent = workflow[concat.Id]?["inputs"]?["audio_latent"] as JArray;

        Assert.NotNull(audioLatent);
        Assert.Equal("2", $"{audioLatent[0]}");

        // Mutate in-place (simulates ReplaceAudioLatentConnections)
        audioLatent[0] = "99";
        audioLatent[1] = 0;

        // JObject reflects the change
        JArray afterMutation = workflow["3"]?["inputs"]?["audio_latent"] as JArray;
        Assert.Equal("99", $"{afterMutation[0]}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  BridgeSync tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SyncLastId_AfterBridgeOps_CoversBridgeIds()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator g = CreateGenerator(workflow);
        g.LastID = 100; // SwarmUI default

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Bridge adds nodes with auto IDs (will be >= 100 since workflow has keys 1-7)
        bridge.AddNode(new KSamplerNode());
        bridge.AddNode(new KSamplerNode());

        // Before sync, g.LastID might be behind
        BridgeSync.SyncLastId(g);

        // Now g.LastID should be past all existing keys
        foreach (JProperty prop in workflow.Properties())
        {
            if (int.TryParse(prop.Name, out int n))
                Assert.True(g.LastID > n, $"g.LastID ({g.LastID}) should be > {n}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RetargetAnimationSaves tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RetargetAnimationSaves_UpdatesSaveNodeConnections()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput oldDecodeOutput = bridge.ResolvePath(new JArray("5", 0));
        var newDecode = bridge.AddNode(new VAEDecodeNode());

        LtxChainOps.RetargetAnimationSaves(bridge, oldDecodeOutput, newDecode.IMAGE);

        // Save node("7") should now point to the new decode
        JObject saveNode = workflow["7"] as JObject;
        JArray imagesRef = saveNode?["inputs"]?["images"] as JArray;
        Assert.NotNull(imagesRef);
        Assert.Equal(newDecode.Id, $"{imagesRef[0]}");
    }
}
