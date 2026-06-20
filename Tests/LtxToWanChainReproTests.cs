using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    // Registers a second, LTX-2 video model alongside the Wan-2.2-14B-i2v bundle so a single clip
    // can chain stage 0 (LTX) -> stage 1 (Wan), mirroring the user payload nonversioned/1.json.
    private static (TestModelBundle Bundle, T2IModel LtxVideoModel) CreateLtxAndWan14bModels()
    {
        TestModelBundle wanBundle = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();
        T2IModelHandler sdHandler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel ltxVideoModel = new(sdHandler, "/tmp", "/tmp/UnitTest_LtxVideo.safetensors", "UnitTest_LtxVideo.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = "unit-video-ltxv2",
                Name = "Unit Video LTXV2",
                CompatClass = T2IModelClassSorter.CompatLtxv2,
                StandardWidth = 1024,
                StandardHeight = 576
            }
        };
        sdHandler.Models[ltxVideoModel.Name] = ltxVideoModel;
        return (wanBundle, ltxVideoModel);
    }

    // Regression test for a node-ID collision: a LoRA confined to the video section (id 2) is reloaded on
    // every video-model load, so on an LTX (stage 0) -> Wan (stage 1) chain the stage-1 LoRA loader was
    // placed via GetStableDynamicID at the same ID the sequential CreateNode/LastID++ allocator then reused
    // for a CLIPTextEncode. That overwrote the LoRA loader, leaving the Wan sampler's `model` input pointing
    // at a CONDITIONING output, which threw "Cannot connect output of type 'CONDITIONING' to input 'model'"
    // when StageRunner.RetargetExistingAnimationSaves re-parsed the workflow.
    [Fact]
    public void Ltx_then_wan_stage_chain_with_video_section_lora_does_not_collide_node_ids()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        (TestModelBundle models, T2IModel ltxVideoModel) = CreateLtxAndWan14bModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel videoLora = new(loraHandler, "/tmp", "/tmp/UnitTest_VideoLora.safetensors", "UnitTest_VideoLora.safetensors");
        loraHandler.Models[videoLora.Name] = videoLora;

        JObject ltxStage = MakeStage(ltxVideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        ltxStage["refStrengths"] = new JArray(1.0);
        JObject wanStage = MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        wanStage["refStrengths"] = new JArray(0.8);
        string stagesJson = new JArray(MakeClipWithRefs([MakeRef("Refiner", frame: 1)], ltxStage, wanStage)).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, ltxVideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        // LTX LoRA confined to the video section (id 2), as in the user payload — loads on every
        // video-model load, including the Wan stage (an orphan LTX LoRA over a Wan model).
        input.Set(T2IParamTypes.Loras, ["UnitTest_VideoLora"]);
        input.Set(T2IParamTypes.LoraWeights, ["1"]);
        input.Set(T2IParamTypes.LoraSectionConfinement, ["2"]);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        // Pre-fix this re-parse threw on the corrupted model<-CONDITIONING edge.
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = [.. bridge.Graph.NodesOfType<SwarmKSamplerNode>()];
        Assert.Equal(2, samplers.Count);
        foreach (SwarmKSamplerNode sampler in samplers)
        {
            ComfyNode modelSource = sampler.Model.Connection?.Node;
            Assert.NotNull(modelSource);
            Assert.IsNotType<CLIPTextEncodeNode>(modelSource);
        }
    }

    // Regression test for the disjoint-branch bug: on a single clip with refs=[Refiner] and stages
    // LTX (stage 0) -> Wan (stage 1), the clip's "Refiner" image reference was applied to BOTH stages.
    // For the non-first Wan stage that re-anchored its start image (and thus the VAE-encoded
    // SwarmKSampler.latent_image the core image-to-video path derives from it) back to the base/refiner
    // image, leaving the LTX stage's VAE Decode feeding nothing — two disconnected branches. The Wan
    // stage must instead continue from the LTX stage output, re-encoded into the Wan latent space.
    [Fact]
    public void Ltx_then_wan_stage_chain_feeds_wan_sampler_latent_from_ltx_output_not_base_image()
    {
        using SwarmUiTestContext _ = new();
        (TestModelBundle models, T2IModel ltxVideoModel) = CreateLtxAndWan14bModels();

        JObject ltxStage = MakeStage(ltxVideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        ltxStage["refStrengths"] = new JArray(1.0);
        JObject wanStage = MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        wanStage["refStrengths"] = new JArray(0.8);
        string stagesJson = new JArray(MakeClipWithRefs([MakeRef("Refiner", frame: 1)], ltxStage, wanStage)).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, ltxVideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Samplers are ordered by node id; the LTX stage (stage 0) is created before the Wan stage (stage 1).
        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        SwarmKSamplerNode ltxSampler = samplers[0];
        SwarmKSamplerNode wanSampler = samplers[1];

        // The Wan sampler's latent_image must chain upstream through the LTX stage sampler (its decoded
        // output, re-encoded) — not branch off the base/refiner image while bypassing the LTX stage.
        ComfyNode wanLatentSource = wanSampler.LatentImage.Connection?.Node;
        Assert.NotNull(wanLatentSource);
        Assert.True(
            ReachesUpstream(bridge, wanLatentSource, ltxSampler.Id),
            "Wan sampler latent_image must chain upstream through the LTX stage output, not the base/refiner image.");

        // The hand-off must be a VAE re-encode (LTX decoded image -> Wan latent space) that sits between the
        // two stages, confirming the LTX output is "piped to Wan's SwarmKSampler.latent_image after encoding".
        Assert.Contains(
            bridge.Graph.NodesOfType<VAEEncodeNode>(),
            encode => ReachesUpstream(bridge, encode, ltxSampler.Id)
                && bridge.Graph.IsReachableUpstream(wanSampler, encode.Id));
    }

    // The Wan continuation stage re-encodes the prior LTX video; the core image-to-video path then resets to
    // the start image (start_at_step > 0) and discards that re-encode, which used to leave an ImageFromBatch
    // (and the extra decode of the LTX video it read from) dangling as a disjoint dead-end branch. The stage
    // runner must prune those so the LTXVSeparateAVLatent video latent is not decoded twice with one side dead.
    [Fact]
    public void Ltx_then_wan_stage_chain_prunes_discarded_source_reencode_and_duplicate_decode()
    {
        using SwarmUiTestContext _ = new();
        (TestModelBundle models, T2IModel ltxVideoModel) = CreateLtxAndWan14bModels();

        JObject ltxStage = MakeStage(ltxVideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        ltxStage["refStrengths"] = new JArray(1.0);
        JObject wanStage = MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        wanStage["refStrengths"] = new JArray(0.8);
        string stagesJson = new JArray(MakeClipWithRefs([MakeRef("Refiner", frame: 1)], ltxStage, wanStage)).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, ltxVideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The discarded source re-encode is an ImageFromBatch -> VAEEncode chain; both node types are always
        // intermediate, so a dangling one means the orphan branch (and the duplicate decode of the LTX video it
        // reads from) was left behind. The live Wan re-encode VAEEncode and the start-image ImageFromBatch both
        // feed the Wan sampler, so a correctly pruned graph has no dangling node of either type.
        foreach (VAEEncodeNode encode in bridge.Graph.NodesOfType<VAEEncodeNode>())
        {
            Assert.True(
                bridge.Graph.FindInputsConnectedTo(encode.LATENT).Any(),
                $"VAEEncode '{encode.Id}' is dangling; the discarded source re-encode latent was not pruned.");
        }
        foreach (ImageFromBatchNode batch in bridge.Graph.NodesOfType<ImageFromBatchNode>())
        {
            Assert.True(
                bridge.Graph.FindInputsConnectedTo(batch.IMAGE).Any(),
                $"ImageFromBatch '{batch.Id}' is a dead-end; the discarded source re-encode branch was not pruned.");
        }
    }

    // When the Wan continuation stage keeps the prior LTX video's resolution and frame count, the core
    // image-to-video path's start-image ImageScale ("Upscale Image" with no upscaling) and frame-limit
    // ImageFromBatch (length == frames) are identity pass-throughs and must be collapsed: the re-encoded
    // latent reads the prior-video decode directly, and the only start-image batch is the length-1 first frame.
    [Fact]
    public void Ltx_then_wan_continuation_reads_prior_video_decode_directly_without_noop_scale_or_full_batch()
    {
        using SwarmUiTestContext _ = new();
        (TestModelBundle models, T2IModel ltxVideoModel) = CreateLtxAndWan14bModels();

        JObject ltxStage = MakeStage(ltxVideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        ltxStage["refStrengths"] = new JArray(1.0);
        JObject wanStage = MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10, cfgScale: 1);
        wanStage["refStrengths"] = new JArray(0.8);
        string stagesJson = new JArray(MakeClipWithRefs([MakeRef("Refiner", frame: 1)], ltxStage, wanStage)).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, ltxVideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmKSamplerNode wanSampler = SamplerNodesOrdered(bridge)[1];

        // Re-encoded latent reads the prior-video decode directly — no identity ImageScale, no full-length
        // frame-limit ImageFromBatch between the decode and the VAEEncode.
        VAEEncodeNode latentEncode = Assert.IsType<VAEEncodeNode>(wanSampler.LatentImage.Connection?.Node);
        Assert.IsType<VAEDecodeNode>(latentEncode.Pixels.Connection?.Node);

        // The Wan start image is a single length-1 ImageFromBatch (first frame) reading that same decode directly.
        WanImageToVideoNode wanI2V = Assert.Single(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        ImageFromBatchNode startBatch = Assert.IsType<ImageFromBatchNode>(wanI2V.StartImage.Connection?.Node);
        Assert.Equal(1, startBatch.Length.LiteralAsInt());
        Assert.IsType<VAEDecodeNode>(startBatch.Image.Connection?.Node);
    }
}
