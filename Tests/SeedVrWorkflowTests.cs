using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class SeedVrWorkflowTests
{
    [Fact]
    public async Task Checked_clips_run_core_SeedVR_after_their_final_stage_with_request_settings()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        T2IModel seedVr = TestStubModel.Install(
            Program.T2IModelSets["Stable-Diffusion"],
            "UnitTest_SeedVR2.safetensors");
        seedVr.ModelClass = T2IModelClassSorter.ModelClasses["seedvr2"];
        string vaeName = CommonModels.Known["seedvr2-vae"].FileName;
        fixture.InstallModel("VAE", vaeName);
        Program.T2IModelSets["VAE"].Models[vaeName].ModelClass =
            T2IModelClassSorter.ModelClasses["seedvr2/vae"];

        JObject plain = Fixtures.MakeClip(1.0, fixture.Stage());
        JObject restored = Fixtures.MakeClip(
            1.0,
            fixture.Stage(),
            fixture.Stage());
        restored["useSeedVr"] = true;

        JObject workflow = await fixture.GenerateAsync(
            Fixtures.MakeDocument(plain, restored),
            post =>
            {
                post["seedvrmodel"] = seedVr.Name;
                post["seedvrupscale"] = "1.5";
                post["seedvrupscalemethod"] = "pixel-nearest-exact";
                post["seedvrcolorcorrectionbehavior"] = "wavelet";
                post["seedvrsplitlatent"] = true;
                post["seedvrtemporalvideooverlap"] = "3";
            });

        JObject preprocess = Node(workflow, "SeedVR2Preprocess");
        JObject chunk = Node(workflow, "SeedVR2TemporalChunk");
        JObject merge = Node(workflow, "SeedVR2TemporalMerge");
        JObject postProcess = Node(workflow, "SeedVR2PostProcessing");
        JProperty finalStageSampler = Assert.Single(
            workflow.Properties(),
            property => property.Value["class_type"]?.Value<string>() == "SwarmKSampler"
                && property.Value["inputs"]?["noise_seed"]?.Value<long>()
                    == VideoStagesWorkflowFixture.StageSeed(2));

        Assert.Equal(3, chunk["inputs"]?["temporal_overlap"]?.Value<int>());
        JArray mergeOverlap = Assert.IsType<JArray>(
            merge["inputs"]?["temporal_overlap"]);
        Assert.Equal(
            "SeedVR2TemporalChunk",
            workflow[$"{mergeOverlap[0]}"]?["class_type"]?.Value<string>());
        Assert.Equal(1, mergeOverlap[1]?.Value<int>());
        Assert.Equal(
            "wavelet",
            postProcess["inputs"]?["color_correction_method"]?.Value<string>());
        Assert.True(Reaches(
            workflow,
            preprocess["inputs"]?["resized_images"],
            finalStageSampler.Name));
        Assert.Single(
            workflow.Properties(),
            property => property.Value["class_type"]?.Value<string>()
                    is "UNETLoader" or "CheckpointLoaderSimple"
                && (property.Value["inputs"]?["unet_name"]?.Value<string>()
                    ?? property.Value["inputs"]?["ckpt_name"]?.Value<string>()) == seedVr.Name);
        Assert.Single(
            workflow.Properties(),
            property => property.Value["class_type"]?.Value<string>() == "ImageScale"
                && property.Value["inputs"]?["upscale_method"]?.Value<string>()
                    == "nearest-exact"
                && property.Value["inputs"]?["width"]?.Value<int>() == 768
                && property.Value["inputs"]?["height"]?.Value<int>() == 768);
    }

    private static JObject Node(JObject workflow, string classType) =>
        (JObject)Assert.Single(
            workflow.Properties(),
            property => property.Value["class_type"]?.Value<string>() == classType).Value;

    private static bool Reaches(JObject workflow, JToken path, string targetId)
    {
        HashSet<string> visited = [];
        bool Visit(JToken candidate)
        {
            if (candidate is not JArray { Count: 2 } connection
                || connection[0]?.Type != JTokenType.String
                || connection[1]?.Type != JTokenType.Integer)
            {
                return false;
            }
            string nodeId = connection[0]!.Value<string>();
            if (nodeId == targetId)
            {
                return true;
            }
            if (!visited.Add(nodeId)
                || workflow[nodeId]?["inputs"] is not JObject inputs)
            {
                return false;
            }
            return inputs.Properties().Any(input => Visit(input.Value));
        }
        return Visit(path);
    }
}
