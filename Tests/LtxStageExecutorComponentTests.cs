using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime.Stage;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class LtxStageExecutorComponentTests
{
    public LtxStageExecutorComponentTests()
    {
        // Register the one type the resolver must recognize on each run. This intentionally avoids
        // the assembly registration one-shot, which other test fixtures can reset independently.
        NodeRegistry.Register(VAEDecodeNode.ClassType, static () => new VAEDecodeNode());
    }

    [Fact]
    public void Reusable_latent_resolver_returns_decode_samples_for_the_same_vae_route()
    {
        JObject workflow = BuildDecodeWorkflow(includeSecondVae: false);
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow
        };
        WGNodeData source = new(
            new JArray("3", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Frames = 97
        };
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = generator,
            Frames = 97,
            Vae = new WGNodeData(
                new JArray("1", 2),
                generator,
                WGNodeData.DT_VAE,
                null)
        };
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            IVaeDecode decode = Assert.IsAssignableFrom<IVaeDecode>(
                bridge.ResolvePath(source.Path).Node);
            Assert.NotNull(decode.Samples.Connection);
            Assert.NotNull(decode.Vae.Connection);
            Assert.Same(
                decode.Vae.Connection.Node,
                bridge.ResolvePath(genInfo.Vae.Path).Node);
            Assert.Equal(
                decode.Vae.Connection.SlotIndex,
                bridge.ResolvePath(genInfo.Vae.Path).SlotIndex);
        }

        bool reused = new LtxReusableLatentResolver(generator).TryResolve(
            source,
            genInfo,
            allowDynamicFrameCount: false,
            out JArray latentPath);

        Assert.True(reused);
        Assert.True(JToken.DeepEquals(new JArray("2", 0), latentPath));
    }

    [Fact]
    public void Reusable_latent_resolver_rejects_a_decode_from_another_vae_route()
    {
        JObject workflow = BuildDecodeWorkflow(includeSecondVae: true);
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow
        };
        WGNodeData source = new(
            new JArray("3", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Frames = 97
        };
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = generator,
            Frames = 97,
            Vae = new WGNodeData(
                new JArray("4", 2),
                generator,
                WGNodeData.DT_VAE,
                null)
        };

        bool reused = new LtxReusableLatentResolver(generator).TryResolve(
            source,
            genInfo,
            allowDynamicFrameCount: false,
            out JArray latentPath);

        Assert.False(reused);
        Assert.Null(latentPath);
    }

    private static JObject BuildDecodeWorkflow(bool includeSecondVae)
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        CheckpointLoaderSimpleNode sourceLoader = bridge.AddNode(
            new CheckpointLoaderSimpleNode().With(CkptName: "source.safetensors"),
            "1");
        EmptyLTXVLatentVideoNode latent = bridge.AddNode(
            new EmptyLTXVLatentVideoNode(),
            "2");
        bridge.AddNode(
            new VAEDecodeNode().With(
                Samples: latent.LATENT,
                Vae: sourceLoader.VAE),
            "3");
        if (includeSecondVae)
        {
            bridge.AddNode(
                new CheckpointLoaderSimpleNode().With(CkptName: "stage.safetensors"),
                "4");
        }
        return workflow;
    }
}
