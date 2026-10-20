using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.MiniMax;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageAttentionWorkflowTests
{
    [Fact]
    public async Task Ltx_stages_leave_attention_to_core()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject stage = fixture.Stage();
        stage["modelAttentionBackendEnabled"] = true;
        stage["modelAttentionBackend"] = "comfy kitchen attention";
        stage["useSparseAttentionEnabled"] = true;
        stage["useSparseAttention"] = "vsa";
        JObject post = fixture.Post(MakeDocument(MakeClip(stage)));
        post["modelattentionbackend"] = "pytorch attention";
        post["usesparseattention"] = "sol";
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(post);
        JObject sampler = (JObject)Assert.Single(workflow.Properties(), node =>
            node.Value["class_type"]?.Value<string>() == "SwarmKSampler").Value;
        List<JObject> chain = [];
        JToken link = sampler["inputs"]["model"];
        while (link is JArray)
        {
            JObject node = (JObject)workflow[link[0].Value<string>()];
            chain.Add(node);
            link = node["inputs"]?["model"];
        }
        JObject sparse = Assert.Single(chain, node =>
            node["class_type"].Value<string>() == "BlockSparseAttention");
        Assert.Equal("sol-attn", sparse["inputs"]["selection"].Value<string>());
        JObject backend = Assert.Single(chain, node =>
            node["class_type"].Value<string>() == "ModelAttentionBackend");
        Assert.Equal("pytorch attention", backend["inputs"]["attention"].Value<string>());
    }

    [Theory]
    [InlineData(false, "vsa", "vsa", 0.0)]
    [InlineData(true, "vsa", "vsa", 0.0)]
    [InlineData(false, "sol", "sol-attn", 0.2)]
    [InlineData(false, "topk", "sla", 0.2)]
    [InlineData(false, "None", null, 0.0)]
    public async Task Each_stage_uses_its_own_core_attention_chain(
        bool imageToVideo, string sparse, string selection, double startPercent)
    {
        using MiniMaxWorkflowFixture fixture = imageToVideo
            ? MiniMaxWorkflowFixture.CreateWithBaseModel()
            : MiniMaxWorkflowFixture.Create();
        JObject first = fixture.Stage(steps: 8);
        first["modelAttentionBackendEnabled"] = true;
        first["modelAttentionBackend"] = "comfy kitchen attention";
        first["useSparseAttentionEnabled"] = true;
        first["useSparseAttention"] = sparse;
        first["h3AttentionWindowSeconds"] = 2.5;
        JObject second = fixture.Stage("PreviousStage", control: 0.5, steps: 10);
        second["modelAttentionBackendEnabled"] = false;
        second["modelAttentionBackend"] = "comfy kitchen attention";
        second["useSparseAttentionEnabled"] = false;
        second["useSparseAttention"] = "vsa";
        JObject clip = MakeClip(first, second);
        clip["duration"] = 1.0;
        JObject document = MakeDocument(clip);
        JObject post = imageToVideo ? fixture.ImageToVideoPost(document) : fixture.Post(document);
        post["modelattentionbackend"] = "pytorch attention";
        post["usesparseattention"] = "sol";

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                post, extraFeatures: [MiniMaxAttentionWindowGraph.FeatureFlag]);

        JProperty[] samplers = [.. workflow.Properties().Where(node =>
            node.Value["class_type"]?.Value<string>() == "SwarmKSampler"
            && node.Value["inputs"]?["steps"]?.Value<int>() is 8 or 10)];
        Assert.Equal(2, samplers.Length);
        JObject firstSampler = (JObject)Assert.Single(samplers, node =>
            node.Value["inputs"]["steps"].Value<int>() == 8).Value["inputs"];
        JObject window = (JObject)workflow[firstSampler["model"][0].Value<string>()];
        Assert.Equal("H3WindowAttentionPatch", window["class_type"].Value<string>());
        Assert.Equal(2.5, window["inputs"]["window_seconds"].Value<double>());
        JObject model = (JObject)workflow[window["inputs"]["model"][0].Value<string>()];
        if (selection is not null)
        {
            Assert.Equal("BlockSparseAttention", model["class_type"].Value<string>());
            JObject inputs = (JObject)model["inputs"];
            Assert.Equal(selection, inputs["selection"].Value<string>());
            Assert.Equal(startPercent, inputs["start_percent"].Value<double>());
            Assert.Equal(1, inputs["end_percent"].Value<double>());
            Assert.Equal(256, inputs["extra_tokens"].Value<int>());
            if (sparse == "sol")
            {
                Assert.Equal(1.3, inputs["selection.tau"].Value<double>());
            }
            if (sparse is "vsa" or "topk")
            {
                Assert.Equal(10, inputs["selection.keep_percent"].Value<double>());
                Assert.Equal("", inputs["dense_blocks"].Value<string>());
                Assert.Equal(12288, inputs["min_tokens"].Value<int>());
                Assert.Equal("exact_kv_and_rows", inputs["sink_conditioning"].Value<string>());
                Assert.False(inputs["verbose"].Value<bool>());
            }
            model = (JObject)workflow[inputs["model"][0].Value<string>()];
        }
        Assert.Equal("ModelAttentionBackend", model["class_type"].Value<string>());
        Assert.Equal("comfy kitchen attention", model["inputs"]["attention"].Value<string>());
        JObject secondSampler = (JObject)Assert.Single(samplers, node =>
            node.Value["inputs"]["steps"].Value<int>() == 10).Value["inputs"];
        JObject secondModel = (JObject)workflow[secondSampler["model"][0].Value<string>()];
        Assert.Equal("MiniMaxH3SigmaShift", secondModel["class_type"].Value<string>());
        Assert.Equal("UNETLoader", workflow[secondModel["inputs"]["model"][0].Value<string>()]["class_type"].Value<string>());
        Assert.Equal("pytorch attention", generator.UserInput.Get(ComfyUIBackendExtension.ModelAttentionBackend));
        Assert.Equal("sol", generator.UserInput.Get(ComfyUIBackendExtension.UseSparseAttention));
    }

    [Theory]
    [InlineData("modelAttentionBackend", "pytorch attention", "comfy kitchen attention")]
    [InlineData("useSparseAttention", "sol", "vsa")]
    public async Task Wan_noise_pair_keeps_distinct_stage_attention(string key, string highValue, string lowValue)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject high = MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8);
        JObject low = MakeStage(fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: 8);
        high[key] = highValue;
        high[key + "Enabled"] = true;
        low[key] = lowValue;
        low[key + "Enabled"] = true;
        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(MakeClip(high, low)));
        string classType = key == "modelAttentionBackend" ? "ModelAttentionBackend" : "BlockSparseAttention";
        string valueKey = key == "modelAttentionBackend" ? "attention" : "selection";
        string[] values = [.. workflow.Properties()
            .Where(node => node.Value["class_type"]?.Value<string>() == classType)
            .Select(node => node.Value["inputs"][valueKey].Value<string>())];
        Assert.Contains(highValue == "sol" ? "sol-attn" : highValue, values);
        Assert.Contains(lowValue, values);
    }
}
