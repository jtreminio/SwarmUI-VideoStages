using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;
using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class WorkflowGraphTests
{
    private static JObject BuildWrapperWorkflow()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Stubs for upstream/crosslink references the test fixtures don't define
        // typed nodes for. The graph walks under test only traverse 200 → 201 →
        // 202 → 204 → 9, so these stubs only need to exist as endpoints.
        UnknownNode stub100 = bridge.AddStub("StubLatent", "100").WithOutputs("LATENT");
        UnknownNode stub104 = bridge.AddStub("StubVae", "104").WithOutputs(WGNodeData.DT_VAE);
        UnknownNode stub203 = bridge.AddStub("StubAudio", "203").WithOutputs(WGNodeData.DT_AUDIO);

        KSamplerNode sampler = new();
        sampler.LatentImage.ConnectToUntyped(stub100.GetOutput(0));
        bridge.AddNode(sampler, "200");

        LTXVSeparateAVLatentNode separate = new();
        separate.AvLatent.ConnectTo(sampler.LATENT);
        bridge.AddNode(separate, "201");

        VAEDecodeTiledNode decode = new();
        decode.Samples.ConnectTo(separate.VideoLatent);
        decode.Vae.ConnectToUntyped(stub104.GetOutput(0));
        bridge.AddNode(decode, "202");

        var trim = new SwarmTrimFramesNode().With(TrimStart: 1, TrimEnd: 1);
        trim.Image.ConnectTo(decode.IMAGE);
        bridge.AddNode(trim, "204");

        SwarmSaveAnimationWSNode save = new();
        save.Images.ConnectTo(trim.IMAGE);
        save.Audio.ConnectToUntyped(stub203.GetOutput(0));
        bridge.AddNode(save, "9");

        return workflow;
    }

    [Fact]
    public void FindNearestDownstream_finds_save_animation_through_wrapper_chain()
    {
        JObject workflow = BuildWrapperWorkflow();
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("202", 0));

        SwarmSaveAnimationWSNode save = bridge.Graph.FindNearestDownstream<SwarmSaveAnimationWSNode>(decodeOutput);

        Assert.NotNull(save);
        Assert.Equal("9", save.Id);
    }

    [Fact]
    public void FindNearestDownstream_finds_decode_from_latent_output()
    {
        JObject workflow = BuildWrapperWorkflow();
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput latentOutput = bridge.ResolvePath(new JArray("200", 0));

        IVaeDecode? decode = bridge.Graph.FindNearestDownstream<IVaeDecode>(latentOutput);

        Assert.NotNull(decode);
        Assert.Equal("202", decode.Id);
    }
}
