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
public class LtxPostVideoChainComponentsTests
{
    public LtxPostVideoChainComponentsTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void InspectorCapturesExpectedGraphState()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        MediaRef media = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO,
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24
        };

        LtxChainCapture inspected = LtxPostVideoChainInspector.TryCapture(
            bridge,
            media,
            currentAudioVae: null,
            useReusedAudio: true);
        Assert.NotNull(inspected);
        Assert.Equal("5", inspected.DecodeId);
        Assert.Equal("4", inspected.SeparateId);
        Assert.Equal("6", inspected.AudioDecodeId);
        Assert.Equal("2", inspected.AudioVaeSource.Node.Id);
        Assert.False(inspected.HasPostDecodeWrappers);
    }

    [Fact]
    public void CaptureCreatesStageInputWithCapturedMetadataAndAudio()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = new(
            new JArray("5", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24,
        };
        LtxPostVideoChainCapture capture = LtxPostVideoChainCapture.TryCapture(generator);
        Assert.NotNull(capture);

        WGNodeData result = capture.CreateStageInput();

        Assert.Equal(WGNodeData.DT_LATENT_AUDIOVIDEO, result.DataType);
        Assert.True(JToken.DeepEquals(capture.State.AvLatentPath, result.Path));
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.Equal(97, result.Frames);
        Assert.Equal(24, result.FPS);
        Assert.NotNull(result.AttachedAudio);
        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, result.AttachedAudio.DataType);
        Assert.True(JToken.DeepEquals(capture.State.AudioLatentPath, result.AttachedAudio.Path));
        Assert.Null(typeof(LtxPostVideoChainCapture).Assembly.GetType(
            "VideoStages.Architectures.Ltx2.LtxStageInputArtifactFactory"));
    }

    [Fact]
    public void AudioResolverPrefersRememberedAudioWhenCaptureRequestsReuse()
    {
        WorkflowGenerator generator = CreateGenerator(new JObject());
        LtxPostVideoChainState state = CreateState(generator) with
        {
            UseReusedAudioLatent = true
        };
        Ltx2ClipAudioReuseState audioReuse = new();
        audioReuse.Remember(new JArray("remembered", 1));
        LtxAudioReferenceResolver resolver = new(generator, audioReuse, state);

        WGNodeData result = resolver.CreateSourceAudioReference();

        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, result.DataType);
        Assert.Equal("remembered", $"{result.Path[0]}");
        Assert.Equal(1L, (long)result.Path[1]);
        Assert.NotSame(audioReuse.ReusedAudioPath, result.Path);
    }

    [Fact]
    public void AudioResolverPrefersPreparedWindowedLatentOverCapturedNativeAudio()
    {
        WorkflowGenerator generator = CreateGenerator(new JObject());
        LtxPostVideoChainState state = CreateState(generator);
        state.CurrentOutputMedia.AttachedAudio = new WGNodeData(
            new JArray("prepared-window-mask", 0),
            generator,
            WGNodeData.DT_LATENT_AUDIO,
            null)
        {
            Frames = 97,
            FPS = 24
        };
        LtxAudioReferenceResolver resolver = new(generator, audioReuse: null, state);

        WGNodeData result = resolver.CreateSourceAudioReference();

        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, result.DataType);
        Assert.Equal("prepared-window-mask", $"{result.Path[0]}");
        Assert.Equal(0L, (long)result.Path[1]);
        Assert.Equal(97, result.Frames);
        Assert.Equal(24, result.FPS);
        Assert.NotSame(state.CurrentOutputMedia.AttachedAudio.Path, result.Path);
    }

    [Fact]
    public void RebuilderMissingStageOutputFailsClosedWithoutGraphMutation()
    {
        JObject workflow = BuildLtxWorkflow();
        JObject before = (JObject)workflow.DeepClone();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        MediaRef current = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO
        };
        LtxChainCapture capture = LtxPostVideoChainInspector.TryCapture(
            bridge,
            current,
            currentAudioVae: null,
            useReusedAudio: false);
        MediaRef vae = new()
        {
            Output = bridge.ResolvePath(new JArray("1", 2)),
            DataType = WGNodeData.DT_VAE
        };

        MediaRef result = LtxPostChainRebuilder.SpliceCurrentOutput(
            bridge,
            capture,
            new MediaRef
            {
                Output = null,
                DataType = WGNodeData.DT_LATENT_AUDIOVIDEO
            },
            vae,
            new LtxDecodeConfig(UseTiledDecode: false));

        Assert.Null(result);
        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void RebuilderMissingVaeFailsClosedWithoutCreatingAnOrphanSeparate()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        MediaRef current = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO
        };
        LtxChainCapture capture = LtxPostVideoChainInspector.TryCapture(
            bridge,
            current,
            currentAudioVae: null,
            useReusedAudio: false);
        KSamplerNode sampler = bridge.AddNode(new KSamplerNode());
        MediaRef stageOutput = new()
        {
            Output = sampler.LATENT,
            DataType = WGNodeData.DT_LATENT_AUDIOVIDEO
        };
        JObject before = (JObject)workflow.DeepClone();

        MediaRef result = LtxPostChainRebuilder.SpliceCurrentOutput(
            bridge,
            capture,
            stageOutput,
            new MediaRef { Output = null, DataType = WGNodeData.DT_VAE },
            new LtxDecodeConfig(UseTiledDecode: false));

        Assert.Null(result);
        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void RebuilderMissingCapturedDecodeFailsClosedWithoutCreatingAnOrphanSeparate()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        MediaRef current = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO
        };
        LtxChainCapture capture = LtxPostVideoChainInspector.TryCapture(
            bridge,
            current,
            currentAudioVae: null,
            useReusedAudio: false) with
        {
            DecodeId = "missing-decode"
        };
        KSamplerNode sampler = bridge.AddNode(new KSamplerNode());
        MediaRef stageOutput = new()
        {
            Output = sampler.LATENT,
            DataType = WGNodeData.DT_LATENT_AUDIOVIDEO
        };
        MediaRef vae = new()
        {
            Output = bridge.ResolvePath(new JArray("1", 2)),
            DataType = WGNodeData.DT_VAE
        };
        JObject before = (JObject)workflow.DeepClone();

        MediaRef result = LtxPostChainRebuilder.SpliceCurrentOutput(
            bridge,
            capture,
            stageOutput,
            vae,
            new LtxDecodeConfig(UseTiledDecode: false));

        Assert.Null(result);
        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void DedicatedRebuilderMissingAudioVaeFailsClosedWithoutGraphMutation()
    {
        JObject workflow = BuildLtxWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        MediaRef current = new()
        {
            Output = bridge.ResolvePath(new JArray("5", 0)),
            DataType = WGNodeData.DT_VIDEO
        };
        LtxChainCapture capture = LtxPostVideoChainInspector.TryCapture(
            bridge,
            current,
            currentAudioVae: null,
            useReusedAudio: false) with
        {
            AudioVaeSource = null
        };
        KSamplerNode sampler = bridge.AddNode(new KSamplerNode());
        MediaRef stageOutput = new()
        {
            Output = sampler.LATENT,
            DataType = WGNodeData.DT_LATENT_AUDIOVIDEO
        };
        MediaRef vae = new()
        {
            Output = bridge.ResolvePath(new JArray("1", 2)),
            DataType = WGNodeData.DT_VAE
        };
        JObject before = (JObject)workflow.DeepClone();

        MediaRef result = LtxPostChainRebuilder.SpliceCurrentOutputToDedicatedBranch(
            bridge,
            capture,
            stageOutput,
            vae,
            new LtxDecodeConfig(UseTiledDecode: false),
            512,
            512,
            25,
            24);

        Assert.Null(result);
        Assert.True(JToken.DeepEquals(before, workflow));
    }

    private static LtxPostVideoChainState CreateState(WorkflowGenerator generator)
    {
        WGNodeData current = new(
            new JArray("decode", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24
        };

        return new LtxPostVideoChainState(
            CurrentOutputMedia: current,
            AvLatentPath: new JArray("av", 0),
            AudioLatentPath: new JArray("separate", 1),
            VideoVaePath: new JArray("vae", 0),
            AudioVaePath: new JArray("audio-vae", 0),
            VideoDecodeNodeId: "decode",
            AudioDecodeNodeId: "audio-decode",
            DecodeOutputPath: new JArray("decode", 0),
            HasPostDecodeWrappers: false,
            UseReusedAudioLatent: false);
    }

    private static WorkflowGenerator CreateGenerator(JObject workflow)
    {
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        return new WorkflowGenerator
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow
        };
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
            ["6"] = new JObject
            {
                ["class_type"] = LTXVAudioVAEDecodeNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("4", 1),
                    ["audio_vae"] = new JArray("2", 0)
                }
            }
        };
    }
}
