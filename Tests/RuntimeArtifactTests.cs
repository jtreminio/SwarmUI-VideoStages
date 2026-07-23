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
