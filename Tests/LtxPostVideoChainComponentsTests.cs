using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime.Audio;
using VideoStages.Architectures.Ltx2.Runtime.Chain;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class LtxPostVideoChainComponentsTests
{
    public LtxPostVideoChainComponentsTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void CaptureFindsExpectedGraphState()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");

        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        Assert.NotNull(capture);
        Assert.Equal("5", capture.VideoDecodeNodeId);
        Assert.False(capture.HasPostDecodeWrappers);
    }

    [Fact]
    public void CaptureWithoutAudioDecodeOrCurrentAudioVaeReturnsNullWithoutMutation()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        workflow.Remove("6");
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        JObject before = (JObject)workflow.DeepClone();

        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);

        Assert.Null(capture);
        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void CaptureCreatesStageInputWithCapturedMetadataAndAudio()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        Assert.NotNull(capture);

        WGNodeData result = capture.CreateStageInput();

        Assert.Equal(WGNodeData.DT_LATENT_AUDIOVIDEO, result.DataType);
        Assert.True(JToken.DeepEquals(capture.AvLatentPath, result.Path));
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.Equal(97, result.Frames);
        Assert.Equal(24, result.FPS);
        Assert.NotNull(result.AttachedAudio);
        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, result.AttachedAudio.DataType);
        Assert.Equal(new JArray("4", 1), result.AttachedAudio.Path);
    }

    [Fact]
    public void AudioResolverPrefersRememberedAudioWhenCaptureRequestsReuse()
    {
        WorkflowGenerator generator = CreateGenerator(new JObject());
        WGNodeData current = Video(generator, "decode");
        Ltx2ClipAudioReuseState audioReuse = new();
        audioReuse.Remember(new JArray("remembered", 1));
        LtxAudioReferenceResolver resolver = new(
            generator,
            audioReuse,
            current,
            new JArray("separate", 1),
            useReusedAudioLatent: true);

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
        WGNodeData current = Video(generator, "decode");
        current.AttachedAudio = new WGNodeData(
            new JArray("prepared-window-mask", 0),
            generator,
            WGNodeData.DT_LATENT_AUDIO,
            null)
        {
            Frames = 97,
            FPS = 24
        };
        LtxAudioReferenceResolver resolver = new(
            generator,
            audioReuse: null,
            current,
            new JArray("separate", 1),
            useReusedAudioLatent: false);

        WGNodeData result = resolver.CreateSourceAudioReference();

        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, result.DataType);
        Assert.Equal("prepared-window-mask", $"{result.Path[0]}");
        Assert.Equal(0L, (long)result.Path[1]);
        Assert.Equal(97, result.Frames);
        Assert.Equal(24, result.FPS);
        Assert.NotSame(current.AttachedAudio.Path, result.Path);
    }

    [Fact]
    public void MissingStageOutputFailsClosedWithoutGraphMutation()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        generator.CurrentMedia = Latent(generator, "missing");
        JObject before = (JObject)workflow.DeepClone();

        capture.SpliceCurrentOutput(Vae(generator, "1"));

        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void MissingVaeFailsClosedWithoutCreatingAnOrphanSeparate()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        AddStageOutput(workflow);
        generator.CurrentMedia = Latent(generator, "50");
        JObject before = (JObject)workflow.DeepClone();

        capture.SpliceCurrentOutput(Vae(generator, "missing"));

        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void MissingCapturedDecodeFailsClosedWithoutCreatingAnOrphanSeparate()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        workflow.Remove("5");
        AddStageOutput(workflow);
        generator.CurrentMedia = Latent(generator, "50");
        JObject before = (JObject)workflow.DeepClone();

        capture.SpliceCurrentOutput(Vae(generator, "1"));

        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void DedicatedSpliceMissingAudioVaeFailsClosedWithoutGraphMutation()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        workflow.Remove("2");
        AddStageOutput(workflow);
        generator.CurrentMedia = Latent(generator, "50");
        JObject before = (JObject)workflow.DeepClone();

        capture.SpliceCurrentOutputToDedicatedBranch(
            Vae(generator, "1"),
            512,
            512,
            25,
            24);

        Assert.True(JToken.DeepEquals(before, workflow));
    }

    [Fact]
    public void DedicatedSpliceLeavesCapturedDecodeBranchesUnchanged()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        JToken originalVideoInput = workflow["5"]["inputs"]["samples"].DeepClone();
        JToken originalAudioInput = workflow["6"]["inputs"]["samples"].DeepClone();
        AddStageOutput(workflow);
        generator.CurrentMedia = Latent(generator, "50");

        capture.SpliceCurrentOutputToDedicatedBranch(
            Vae(generator, "1"),
            512,
            384,
            25,
            24);

        Assert.True(JToken.DeepEquals(originalVideoInput, workflow["5"]["inputs"]["samples"]));
        Assert.True(JToken.DeepEquals(originalAudioInput, workflow["6"]["inputs"]["samples"]));
        Assert.NotEqual("5", $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(384, generator.CurrentMedia.Height);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
    }

    [Fact]
    public void DedicatedSpliceUsesCurrentAudioVaeWithoutAnExistingAudioDecode()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        workflow.Remove("6");
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        generator.CurrentAudioVae = new WGNodeData(
            new JArray("2", 0),
            generator,
            WGNodeData.DT_AUDIOVAE,
            null);
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        AddStageOutput(workflow);
        generator.CurrentMedia = Latent(generator, "50");

        capture.SpliceCurrentOutputToDedicatedBranch(
            Vae(generator, "1"),
            512,
            512,
            25,
            24);

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        LTXVAudioVAEDecodeNode audioDecode = bridge.NodeAt<LTXVAudioVAEDecodeNode>(
            generator.CurrentMedia.AttachedAudio.Path);
        Assert.NotNull(audioDecode);
        Assert.Equal("2", audioDecode.AudioVae.Connection.Node.Id);
    }

    [Fact]
    public void OrdinarySplicePreservesPostDecodeWrappers()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
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
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "8");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        AddStageOutput(workflow);
        generator.CurrentMedia = Latent(generator, "50");

        capture.SpliceCurrentOutput(Vae(generator, "1"));

        Assert.Equal(new JArray("8", 0), generator.CurrentMedia.Path);
        Assert.Equal(new JArray("5", 0), workflow["8"]["inputs"]["image"]);
        Assert.NotEqual("4", $"{workflow["5"]["inputs"]["samples"][0]}");
    }

    [Fact]
    public void RepeatedSpliceFailsClosed()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.CurrentMedia = Video(generator, "5");
        LtxPostVideoChain capture = LtxPostVideoChain.TryCapture(generator);
        AddStageOutput(workflow);
        generator.CurrentMedia = Latent(generator, "50");
        WGNodeData vae = Vae(generator, "1");
        capture.SpliceCurrentOutput(vae);
        WGNodeData firstResult = generator.CurrentMedia;
        JObject afterFirstSplice = (JObject)workflow.DeepClone();

        capture.SpliceCurrentOutput(vae);

        Assert.Same(firstResult, generator.CurrentMedia);
        Assert.True(JToken.DeepEquals(afterFirstSplice, workflow));
    }

    private static WGNodeData Video(WorkflowGenerator generator, string nodeId) => new(
        new JArray(nodeId, 0),
        generator,
        WGNodeData.DT_VIDEO,
        null)
    {
        Width = 1280,
        Height = 720,
        Frames = 97,
        FPS = 24
    };

    private static WGNodeData Latent(WorkflowGenerator generator, string nodeId) => new(
        new JArray(nodeId, 0),
        generator,
        WGNodeData.DT_LATENT_AUDIOVIDEO,
        null);

    private static WGNodeData Vae(WorkflowGenerator generator, string nodeId) => new(
        new JArray(nodeId, 2),
        generator,
        WGNodeData.DT_VAE,
        null);

    private static void AddStageOutput(JObject workflow) => workflow["50"] = new JObject
    {
        ["class_type"] = SwarmKSamplerNode.ClassType,
        ["inputs"] = new JObject { ["seed"] = 123 }
    };

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
}
