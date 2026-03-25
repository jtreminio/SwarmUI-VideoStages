using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class WorkflowUtilsTests
{
    private static JObject BuildWrapperWorkflow() => new()
    {
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
    public void Reachability_finds_animation_save_through_wrapper_chain()
    {
        JObject workflow = BuildWrapperWorkflow();

        Assert.True(WorkflowUtils.IsNodeTypeReachableFromOutput(
            workflow,
            new JArray("202", 0),
            "SwarmSaveAnimationWS"));
    }

    [Fact]
    public void Upstream_decode_resolution_finds_decode_through_wrapper_chain()
    {
        JObject workflow = BuildWrapperWorkflow();

        Assert.True(WorkflowUtils.TryResolveNearestUpstreamDecode(
            workflow,
            new JArray("204", 0),
            out WorkflowNode decodeNode));
        Assert.Equal("202", decodeNode.Id);
    }

    [Fact]
    public void Downstream_decode_resolution_finds_decode_through_wrapper_chain()
    {
        JObject workflow = BuildWrapperWorkflow();

        Assert.True(WorkflowUtils.TryResolveNearestDownstreamDecodeOutput(
            workflow,
            new JArray("200", 0),
            out JArray decodeOutput));
        Assert.True(JToken.DeepEquals(decodeOutput, new JArray("202", 0)));
    }
}
