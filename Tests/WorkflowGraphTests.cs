using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class WorkflowGraphTests
{
    private static JObject BuildWrapperWorkflow() => new()
    {
        ["200"] = new JObject()
        {
            ["class_type"] = "KSampler",
            ["inputs"] = new JObject()
            {
                ["latent_image"] = new JArray("100", 0)
            }
        },
        ["201"] = new JObject()
        {
            ["class_type"] = "LTXVSeparateAVLatent",
            ["inputs"] = new JObject()
            {
                ["av_latent"] = new JArray("200", 0)
            }
        },
        ["202"] = new JObject()
        {
            ["class_type"] = "VAEDecodeTiled",
            ["inputs"] = new JObject()
            {
                ["samples"] = new JArray("201", 0),
                ["vae"] = new JArray("104", 0)
            }
        },
        ["204"] = new JObject()
        {
            ["class_type"] = "SwarmTrimFrames",
            ["inputs"] = new JObject()
            {
                ["image"] = new JArray("202", 0),
                ["trim_start"] = 1,
                ["trim_end"] = 1
            }
        },
        ["9"] = new JObject()
        {
            ["class_type"] = "SwarmSaveAnimationWS",
            ["inputs"] = new JObject()
            {
                ["images"] = new JArray("204", 0),
                ["audio"] = new JArray("203", 0)
            }
        }
    };

    [Fact]
    public void FindNearestDownstream_finds_save_animation_through_wrapper_chain()
    {
        JObject workflow = BuildWrapperWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput decodeOutput = bridge.ResolvePath(new JArray("202", 0));

        SwarmSaveAnimationWSNode save = bridge.Graph.FindNearestDownstream<SwarmSaveAnimationWSNode>(decodeOutput);

        Assert.NotNull(save);
        Assert.Equal("9", save.Id);
    }

    [Fact]
    public void FindNearestDownstream_finds_decode_from_latent_output()
    {
        JObject workflow = BuildWrapperWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput latentOutput = bridge.ResolvePath(new JArray("200", 0));

        ComfyNode decode = bridge.Graph.FindNearestDownstream(
            latentOutput,
            n => n is VAEDecodeNode or VAEDecodeTiledNode);

        Assert.NotNull(decode);
        Assert.Equal("202", decode.Id);
    }
}
