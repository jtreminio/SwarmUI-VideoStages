using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Execution;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class RuntimeArtifactTests
{
    [Fact]
    public void Capture_and_publish_round_trip_media_vae_and_audio()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode video = bridge.AddStub("UnitTestVideo", "10").WithOutputs(WGNodeData.DT_VIDEO);
        UnknownNode audio = bridge.AddStub("UnitTestAudio", "11").WithOutputs(WGNodeData.DT_AUDIO);
        UnknownNode vae = bridge.AddStub("UnitTestVae", "12").WithOutputs(WGNodeData.DT_VAE);

        generator.CurrentMedia = Data(generator, "10", WGNodeData.DT_VIDEO);
        generator.CurrentMedia.AttachedAudio =
            Data(generator, "11", WGNodeData.DT_AUDIO);
        generator.CurrentVae = Data(generator, "12", WGNodeData.DT_VAE);

        RuntimeArtifact artifact = RuntimeArtifact.Capture(
            generator,
            bridge);
        generator.CurrentMedia = null;
        generator.CurrentVae = null;

        artifact.PublishTo(generator);

        Assert.True(artifact.HasMedia);
        Assert.Equal("10", $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal("11", $"{generator.CurrentMedia.AttachedAudio.Path[0]}");
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio.DataType);
        Assert.Equal("12", $"{generator.CurrentVae.Path[0]}");
    }

    [Fact]
    public void Capture_without_current_media_is_an_empty_artifact()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        RuntimeArtifact artifact = RuntimeArtifact.Capture(
            generator,
            bridge);

        Assert.False(artifact.HasMedia);
        Assert.Null(artifact.Media);
        Assert.Null(artifact.Vae);
    }

    [Fact]
    public void Captured_null_vae_clears_stale_ambient_vae_on_publish()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        RuntimeArtifact artifact = RuntimeArtifact.Capture(
            generator,
            bridge);
        UnknownNode vae = bridge.AddStub("UnitTestVae", "40").WithOutputs(WGNodeData.DT_VAE);
        generator.CurrentVae = Data(generator, "40", WGNodeData.DT_VAE);

        artifact.PublishTo(generator);

        Assert.Null(generator.CurrentVae);
    }

    [Fact]
    public void Decoded_clip_rejects_forged_non_video_runtime_media_before_neutral_handoff()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode latent = bridge.AddStub("UnitTestLatent", "50").WithOutputs("LATENT");
        RuntimeArtifact artifact = new(
            new MediaRef
            {
                Output = latent.GetOutput(0),
                DataType = WGNodeData.DT_LATENT_VIDEO,
                Width = 512,
                Height = 512,
                Frames = 25,
                FPS = 24,
            },
            Vae: null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => DecodedClipArtifact.FromRuntime(artifact, Clip()));

        Assert.Contains("did not produce decoded video media", error.Message);
    }

    [Fact]
    public void Decoded_clip_rejects_forged_non_decoded_attached_audio_before_neutral_handoff()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode video = bridge.AddStub("UnitTestVideo", "60").WithOutputs(WGNodeData.DT_VIDEO);
        UnknownNode latentAudio = bridge.AddStub("UnitTestAudioLatent", "61").WithOutputs("LATENT");
        RuntimeArtifact artifact = new(
            new MediaRef
            {
                Output = video.GetOutput(0),
                DataType = WGNodeData.DT_VIDEO,
                Width = 512,
                Height = 512,
                Frames = 25,
                FPS = 24,
                AttachedAudio = new MediaRef
                {
                    Output = latentAudio.GetOutput(0),
                    DataType = WGNodeData.DT_LATENT_AUDIO,
                },
            },
            Vae: null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => DecodedClipArtifact.FromRuntime(artifact, Clip()));

        Assert.Contains("did not produce decoded attached audio", error.Message);
    }

    [Fact]
    public void Global_trim_rejects_latent_audio_before_mutating_the_graph()
    {
        JObject workflow = [];
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 1);
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = input,
        };
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            bridge.AddStub("UnitTestVideo", "70").WithOutputs(WGNodeData.DT_VIDEO);
            bridge.AddStub("UnitTestLatentAudio", "71").WithOutputs("LATENT");
        }
        generator.CurrentMedia = Data(generator, "70", WGNodeData.DT_VIDEO);
        generator.CurrentMedia.Width = 512;
        generator.CurrentMedia.Height = 512;
        generator.CurrentMedia.Frames = 25;
        generator.CurrentMedia.FPS = 24;
        generator.CurrentMedia.AttachedAudio =
            Data(generator, "71", WGNodeData.DT_LATENT_AUDIO);
        JObject before = (JObject)workflow.DeepClone();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new GlobalVideoFrameTrimmer(generator).Apply());

        Assert.Contains("is not a decoded audio stream", error.Message);
        Assert.True(JToken.DeepEquals(before, workflow));
        Assert.Equal(new JArray("70", 0), generator.CurrentMedia.Path);
    }

    [Fact]
    public void Global_trim_rejects_unresolved_video_before_mutating_the_graph()
    {
        JObject workflow = [];
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 1);
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = input,
        };
        generator.CurrentMedia = Data(generator, "missing-video", WGNodeData.DT_VIDEO);
        generator.CurrentMedia.Width = 512;
        generator.CurrentMedia.Height = 512;
        generator.CurrentMedia.Frames = 25;
        generator.CurrentMedia.FPS = 24;
        JObject before = (JObject)workflow.DeepClone();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new GlobalVideoFrameTrimmer(generator).Apply());

        Assert.Contains("no decoded output to trim", error.Message);
        Assert.True(JToken.DeepEquals(before, workflow));
        Assert.Equal(new JArray("missing-video", 0), generator.CurrentMedia.Path);
    }

    [Fact]
    public void Global_trim_rejects_unresolved_audio_before_mutating_the_graph()
    {
        JObject workflow = [];
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 1);
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = input,
        };
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            bridge.AddStub("UnitTestVideo", "80").WithOutputs(WGNodeData.DT_VIDEO);
        }
        generator.CurrentMedia = Data(generator, "80", WGNodeData.DT_VIDEO);
        generator.CurrentMedia.Width = 512;
        generator.CurrentMedia.Height = 512;
        generator.CurrentMedia.Frames = 25;
        generator.CurrentMedia.FPS = 24;
        generator.CurrentMedia.AttachedAudio =
            Data(generator, "missing-audio", WGNodeData.DT_AUDIO);
        JObject before = (JObject)workflow.DeepClone();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new GlobalVideoFrameTrimmer(generator).Apply());

        Assert.Contains("attached audio stream required for global frame trim", error.Message);
        Assert.True(JToken.DeepEquals(before, workflow));
        Assert.Equal(new JArray("80", 0), generator.CurrentMedia.Path);
        Assert.Equal(
            new JArray("missing-audio", 0),
            generator.CurrentMedia.AttachedAudio.Path);
    }

    private static ClipPlan Clip() => new(
        ClipId: 7,
        Frames: 25,
        EntryMode: ArchitectureEntryMode.ImageToVideo,
        InitVideo: null,
        Stages: [],
        Audio: null)
    {
        Architecture = Ltx2ArchitectureModule.Instance.Descriptor,
    };

    private static WGNodeData Data(WorkflowGenerator generator, string nodeId, string dataType) =>
        new(new JArray(nodeId, 0), generator, dataType, T2IModelClassSorter.CompatLtxv2);
}
