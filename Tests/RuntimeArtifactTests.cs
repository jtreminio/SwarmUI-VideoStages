using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;
using Xunit;

namespace VideoStages.Tests;

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
            bridge,
            ArtifactOrigin.SourceVideo);
        generator.CurrentMedia = null;
        generator.CurrentVae = null;

        artifact.PublishTo(generator);

        Assert.Equal(ArtifactOrigin.SourceVideo, artifact.Origin);
        Assert.True(artifact.HasMedia);
        Assert.Equal("10", $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal("11", $"{generator.CurrentMedia.AttachedAudio.Path[0]}");
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio.DataType);
        Assert.Equal("12", $"{generator.CurrentVae.Path[0]}");
    }

    [Fact]
    public void Clone_detaches_mutable_media_refs_and_can_change_origin()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode video = bridge.AddStub("UnitTestVideo", "20").WithOutputs(WGNodeData.DT_VIDEO);
        UnknownNode audio = bridge.AddStub("UnitTestAudio", "21").WithOutputs(WGNodeData.DT_LATENT_AUDIO);
        generator.CurrentMedia = Data(generator, "20", WGNodeData.DT_VIDEO);
        generator.CurrentMedia.AttachedAudio =
            Data(generator, "21", WGNodeData.DT_LATENT_AUDIO);

        RuntimeArtifact original = RuntimeArtifact.Capture(
            generator,
            bridge,
            ArtifactOrigin.HostRoot);
        RuntimeArtifact cloned = original.Copy(ArtifactOrigin.StageOutput);

        Assert.NotSame(original.Media, cloned.Media);
        Assert.NotSame(original.Media.AttachedAudio, cloned.Media.AttachedAudio);
        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, cloned.Media.AttachedAudio.DataType);
        Assert.Equal(ArtifactOrigin.StageOutput, cloned.Origin);
        Assert.Equal(ArtifactOrigin.HostRoot, original.Origin);
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
            bridge,
            ArtifactOrigin.HostRoot);

        Assert.False(artifact.HasMedia);
        Assert.Null(artifact.Media);
        Assert.Null(artifact.Vae);
    }

    [Fact]
    public void Publish_with_preserve_host_vae_does_not_clear_ambient_vae()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode video = bridge.AddStub("UnitTestVideo", "30").WithOutputs(WGNodeData.DT_VIDEO);
        UnknownNode vae = bridge.AddStub("UnitTestVae", "31").WithOutputs(WGNodeData.DT_VAE);
        generator.CurrentVae = Data(generator, "31", WGNodeData.DT_VAE);

        RuntimeArtifact artifact = new(
            MediaRef.FromWGNodeData(
                Data(generator, "30", WGNodeData.DT_VIDEO),
                bridge),
            Vae: null,
            ArtifactOrigin.SourceVideo,
            ArtifactVaeDisposition.PreserveHost);

        artifact.PublishTo(generator);

        Assert.Equal("30", $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal("31", $"{generator.CurrentVae.Path[0]}");
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
            bridge,
            ArtifactOrigin.HostRoot);
        UnknownNode vae = bridge.AddStub("UnitTestVae", "40").WithOutputs(WGNodeData.DT_VAE);
        generator.CurrentVae = Data(generator, "40", WGNodeData.DT_VAE);

        artifact.PublishTo(generator);

        Assert.Null(generator.CurrentVae);
    }

    private static WGNodeData Data(WorkflowGenerator generator, string nodeId, string dataType) =>
        new(new JArray(nodeId, 0), generator, dataType, T2IModelClassSorter.CompatLtxv2);
}
