using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.MiniMax;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// MiniMax H3 generated-graph contracts. Stages are identified by sampler seed because node order
/// and upstream reachability do not identify a refining stage.
/// </summary>
[Collection("VideoStagesTests")]
public class MiniMaxGeneratedWorkflowContractTests
{
    [Theory]
    [InlineData(
        "8b",
        "qwen3vl_8b.safetensors",
        "boogu",
        "mmh3-8b-ClipProj-v3-mlp.safetensors")]
    [InlineData(
        "4b",
        "qwen3vl_4b.safetensors",
        "krea2",
        "mmh3-4b-ClipProj-v3-mlp.safetensors")]
    public async Task Selected_text_encoder_retargets_core_loader_then_projects_its_clip(
        string selection,
        string expectedEncoder,
        string expectedLoaderType,
        string expectedProjection)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        fixture.InstallModel("Clip", expectedEncoder);
        string projectionDirectory = Path.Join(
            SwarmUI.Core.Program.ServerSettings.Paths.ActualModelRoot,
            MiniMaxTextEncoderGraph.ProjectionFolder);
        string projectionPath = Path.Join(projectionDirectory, expectedProjection);
        Directory.CreateDirectory(projectionDirectory);
        bool removeProjectionStub = !File.Exists(projectionPath);
        if (removeProjectionStub)
        {
            File.WriteAllBytes(projectionPath, []);
        }
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5));
        clip["duration"] = 1.0;
        clip["h3TextEncoder"] = selection;

        JObject workflow;
        try
        {
            workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.Post(MakeDocument(clip)),
                extraFeatures: [MiniMaxTextEncoderGraph.FeatureFlag]);
        }
        finally
        {
            if (removeProjectionStub)
            {
                File.Delete(projectionPath);
            }
        }
        JProperty projection = Assert.Single(
            workflow.Properties(),
            property => property.Value["class_type"]?.Value<string>()
                == ClipProjApplyNode.ClassType);
        JProperty loader = Assert.Single(
            workflow.Properties(),
            property => property.Value["class_type"]?.Value<string>() == CLIPLoaderNode.ClassType);
        JObject loaderInputs = (JObject)loader.Value["inputs"];
        JObject projectionInputs = (JObject)projection.Value["inputs"];

        Assert.Equal(expectedEncoder, loaderInputs["clip_name"]?.Value<string>());
        Assert.Equal(expectedLoaderType, loaderInputs["type"]?.Value<string>());
        Assert.Equal(expectedProjection, projectionInputs["projection"]?.Value<string>());
        Assert.Equal(loader.Name, projectionInputs["clip"]?[0]?.Value<string>());
        JProperty[] encodes = [.. workflow.Properties().Where(property =>
            property.Value["class_type"]?.Value<string>() == CLIPTextEncodeNode.ClassType)];
        Assert.NotEmpty(encodes);
        Assert.All(
            encodes,
            encode => Assert.Equal(
                projection.Name,
                encode.Value["inputs"]?["clip"]?[0]?.Value<string>()));
    }

    [Fact]
    public async Task Default_text_encoder_keeps_core_MiniMax_conditioning_graph()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["h3TextEncoder"] = "default";

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        JProperty loader = Assert.Single(
            workflow.Properties(),
            property => property.Value["class_type"]?.Value<string>() == CLIPLoaderNode.ClassType);

        Assert.Equal(
            "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors",
            loader.Value["inputs"]?["clip_name"]?.Value<string>());
        Assert.DoesNotContain(workflow.Properties(), property =>
            property.Value["class_type"]?.Value<string>() == ClipProjApplyNode.ClassType);
    }

    [Fact]
    public async Task Selected_text_encoder_requires_ClipProj()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        fixture.InstallModel("Clip", "qwen3vl_8b.safetensors");
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["h3TextEncoder"] = "8b";

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => fixture.GenerateAsync(MakeDocument(clip)));

        Assert.Contains("ClipProj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clip_attention_window_patches_the_model_used_by_every_H3_sampler()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5));
        clip["duration"] = 1.0;
        clip["h3AttentionWindowSeconds"] = 2.5;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraFeatures: [MiniMaxAttentionWindowGraph.FeatureFlag]);
        JProperty[] windows = [.. workflow.Properties().Where(property =>
            property.Value["class_type"]?.Value<string>()
                == H3WindowAttentionPatchNode.ClassType)];
        JProperty[] samplers = [.. workflow.Properties().Where(property =>
            property.Value["class_type"]?.Value<string>() == SwarmKSamplerNode.ClassType)];

        Assert.Single(windows);
        Assert.Equal(2, samplers.Length);
        Assert.All(windows, property =>
        {
            JObject inputs = (JObject)property.Value["inputs"];
            Assert.Equal(2.5, inputs["window_seconds"]?.Value<double>());
            Assert.Equal("0,9,19,29,39,49", inputs["dense_layers"]?.Value<string>());
            Assert.True(inputs["verbose"]?.Value<bool>());
        });
        HashSet<string> windowIds = [.. windows.Select(property => property.Name)];
        Assert.All(samplers, property =>
        {
            JArray model = (JArray)property.Value["inputs"]?["model"];
            Assert.Contains(model?[0]?.Value<string>(), windowIds);
        });
    }

    [Fact]
    public async Task Clip_attention_window_stays_off_at_zero_or_without_JuanAttn()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["h3AttentionWindowSeconds"] = 2.5;

        JObject withoutFeature = await fixture.GenerateAsync(MakeDocument(clip));
        clip["h3AttentionWindowSeconds"] = 0;
        JObject atZero = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraFeatures: [MiniMaxAttentionWindowGraph.FeatureFlag]);

        Assert.DoesNotContain(withoutFeature.Properties(), property =>
            property.Value["class_type"]?.Value<string>()
                == H3WindowAttentionPatchNode.ClassType);
        Assert.DoesNotContain(atZero.Properties(), property =>
            property.Value["class_type"]?.Value<string>()
                == H3WindowAttentionPatchNode.ClassType);
    }

    [Fact]
    public async Task Basic_text_to_video_can_be_generated_from_the_Comfy_API_POST_body()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        VAEDecodeAudioNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeAudioNode>());

        Assert.Same(latent, sampler.LatentImage.Connection?.Node);
        Assert.Equal(MiniMaxWorkflowFixture.Steps, sampler.Steps.LiteralAsInt());

        Assert.Equal(39, latent.Length.LiteralAsInt());

        live.AssertAllLive(latent, sampler, audioDecode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A refine stage re-encodes the decoded video, which collapses back onto the sampler's own
    /// latent and leaves stage 0's decode unused for core's cleanup to prune — the audio half must
    /// survive that.
    /// </summary>
    [Fact]
    public async Task A_second_stage_refines_the_joint_latent_that_survives_core_cleanup()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(), fixture.Stage(control: 0.5));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count());
        Assert.Equal(0, StageSampler(bridge, 0).StartAtStep.LiteralAsInt());

        SwarmKSamplerNode refine = StageSampler(bridge, 1);
        Assert.Equal(4, refine.StartAtStep.LiteralAsInt());
        LTXVConcatAVLatentNode joint = Assert.IsType<LTXVConcatAVLatentNode>(
            refine.LatentImage.Connection?.Node);

        SetLatentNoiseMaskNode audioMask = Assert.IsType<SetLatentNoiseMaskNode>(
            joint.AudioLatent.Connection?.Node);
        SolidMaskNode solidMask = Assert.IsType<SolidMaskNode>(audioMask.Mask.Connection?.Node);
        Assert.Equal(0, solidMask.Value.LiteralAsDouble());

        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
        Assert.Single(bridge.Graph.NodesOfType<SolidMaskNode>());
        // The refine stage masks the carried audio rather than re-encoding it.
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());

        live.AssertAllLive(joint, audioMask, solidMask, refine);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Three places emit a SwarmTrimFrames node: core's save step, core's image-to-video step,
    /// and the extension's global trimmer. Only the production list runs all three.
    /// </summary>
    [Fact]
    public async Task Three_stages_publish_intermediates_and_trim_only_the_final_output()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5),
            fixture.Stage("PreviousStage", control: 0.25));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post =>
            {
                post["outputintermediateimages"] = true;
                post["trimvideostartframes"] = 4;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        SwarmKSamplerNode third = StageSampler(bridge, 2);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, trim, third.Id),
            "The trim does not sit downstream of the final stage.");

        SwarmSaveAnimationWSNode finalSave = live.FinalVideoSave();
        Assert.True(
            ReachesUpstream(bridge, finalSave.Images.Connection?.Node, trim.Id),
            "The published save does not read the trimmed output.");

        SwarmSaveAnimationWSNode[] saves =
            [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, first.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, second.Id));
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, second.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, third.Id));
        Assert.All(
            saves.Where(save => !ReferenceEquals(save, finalSave)),
            save => Assert.False(
                ReachesUpstream(bridge, save.Images.Connection?.Node, trim.Id),
                "An intermediate save reads the trimmed output; only the final one should."));

        live.AssertAllLive(first, second, third, trim);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }

    /// <summary>
    /// Persisted clip LoRAs reach every stage while prompt confinement still selects one stage.
    /// </summary>
    [Fact]
    public async Task Persisted_clip_LoRA_reaches_every_stage_and_prompt_LoRA_stays_scoped()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_MiniMax_Prompt.safetensors")
            .InstallModel("LoRA", "UnitTest_MiniMax_Persisted.safetensors");

        JObject first = fixture.Stage();
        JObject clip = MakeClip(first, fixture.Stage("PreviousStage", control: 0.5, steps: 9));
        clip["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_MiniMax_Persisted",
            ["weight"] = 0.6,
            ["textEncoderWeight"] = 0.7,
        });
        clip["duration"] = 1.0;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(clip),
                    post => post["prompt"] =
                        "global <videoclip[0,0]><lora:UnitTest_MiniMax_Prompt:0.4:0.8>"));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode secondSampler = StageSampler(bridge, 1);
        Assert.Equal(MiniMaxWorkflowFixture.Steps, firstSampler.Steps.LiteralAsInt());
        Assert.Equal(9, secondSampler.Steps.LiteralAsInt());

        ComfyNode[] loras = [.. LoraLoaderNodesOf(bridge)];
        ComfyNode prompt = Assert.Single(
            loras,
            lora => lora.FindInput("lora_name").LiteralAsString()
                == "UnitTest_MiniMax_Prompt.safetensors");
        ComfyNode[] persisted =
        [
            .. loras.Where(lora => lora.FindInput("lora_name").LiteralAsString()
                == "UnitTest_MiniMax_Persisted.safetensors"),
        ];
        Assert.NotEmpty(persisted);
        Assert.True(ReachesUpstream(bridge, firstSampler.Model.Connection?.Node, prompt.Id));
        Assert.False(ReachesUpstream(bridge, secondSampler.Model.Connection?.Node, prompt.Id));
        Assert.All(
            [firstSampler, secondSampler],
            sampler => Assert.Contains(
                persisted,
                lora => ReachesUpstream(bridge, sampler.Model.Connection?.Node, lora.Id)));
        Assert.All(
            persisted,
            lora => Assert.Equal(0.6, lora.FindInput("strength_model").LiteralAsDouble()));

        Assert.True(generator.NodeHelpers.TryGetValue(
            $"modelloader_{fixture.Model.Name}_image2video",
            out string cachedLoader));
        Assert.Equal(secondSampler.Model.Connection?.Node.Id, cachedLoader.Split(':')[0]);

        live.AssertAllLive([firstSampler, secondSampler, .. loras]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Framing reuse is keyed on source plus size plus method, so a second stage resolution adds
    /// a second SwarmFrameImage — and each must reach its own stage.
    /// </summary>
    [Fact]
    public async Task A_final_frame_reference_is_reframed_for_each_stage_resolution()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5));
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(UploadedReference("TEFTVA==", fromEnd: true));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());

        SwarmFrameImageNode framed512 = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameImageNode>(),
            node => node.Width.LiteralAsInt() == 512);
        SwarmFrameImageNode framed768 = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameImageNode>(),
            node => node.Width.LiteralAsInt() == 768);
        Assert.All(
            new[] { framed512, framed768 },
            node =>
            {
                Assert.Same(upload, node.ImagesInput.Connection?.Node);
                Assert.Equal("fit-green", node.Method.LiteralAsString());
            });

        // The authored reference is fromEnd, so each stage must consume its framing through the
        // keyframe node's last_frame slot — reachability from the sampler alone would not say which
        // slot, nor that first_frame stayed unwired.
        Assert.All(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>(),
            keyframes => Assert.Null(keyframes.FirstFrame.Connection));
        Assert.Equal(
            [512, 768],
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>()
                .Select(keyframes => Assert.IsType<SwarmFrameImageNode>(
                    keyframes.LastFrame.Connection?.Node).Width.LiteralAsInt())
                .Order());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.True(ReachesUpstream(bridge, first.Positive.Connection?.Node, framed512.Id));
        Assert.True(ReachesUpstream(bridge, second.Positive.Connection?.Node, framed768.Id));

        live.AssertAllLive(upload, framed512, framed768, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A refine stage re-encodes decoded frames that have already drifted from the authored opening
    /// image, so dropping the first keyframe after stage 0 leaves nothing holding frame 0 — and an
    /// upscaling stage samples at a resolution the stage-0 framing does not fit.
    /// </summary>
    [Fact]
    public async Task An_opening_frame_reference_is_reframed_for_each_stage_resolution()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5));
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(UploadedReference("RklSU1Q="));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("RklSU1Q=", upload.ImageBase64.LiteralAsString());

        SwarmFrameImageNode framed512 = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameImageNode>(),
            node => node.Width.LiteralAsInt() == 512);
        SwarmFrameImageNode framed768 = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameImageNode>(),
            node => node.Width.LiteralAsInt() == 768);
        Assert.All(
            new[] { framed512, framed768 },
            node => Assert.Same(upload, node.ImagesInput.Connection?.Node));

        Assert.All(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>(),
            keyframes => Assert.Null(keyframes.LastFrame.Connection));
        Assert.Equal(
            [512, 768],
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>()
                .Select(keyframes => Assert.IsType<SwarmFrameImageNode>(
                    keyframes.FirstFrame.Connection?.Node).Width.LiteralAsInt())
                .Order());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.True(ReachesUpstream(bridge, first.Positive.Connection?.Node, framed512.Id));
        Assert.True(ReachesUpstream(bridge, second.Positive.Connection?.Node, framed768.Id));
        AssertKeyframesReadTheSampledJointLatent(first, second);

        live.AssertAllLive(upload, framed512, framed768, first, second);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Arbitrary_frame_references_chain_core_guides_around_the_sampled_joint_latent()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(
            UploadedReference("U1RBUlQ=", frame: 9),
            UploadedReference("RU5E", fromEnd: true, frame: 4));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        MiniMaxH3AddGuideNode[] guides =
            [.. bridge.Graph.NodesOfType<MiniMaxH3AddGuideNode>()];
        Assert.Equal([9, -4], guides.Select(guide => guide.FrameIdx.LiteralAsInt()));
        Assert.All(guides, guide =>
        {
            Assert.NotNull(guide.Image.Connection);
            Assert.NotNull(guide.Vae.Connection);
        });
        Assert.Same(guides[0], guides[1].PositiveInput.Connection?.Node);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(guides[1], sampler.Positive.Connection?.Node);
        Assert.All(
            guides,
            guide => Assert.Same(sampler.LatentImage.Connection, guide.Latent.Connection));

        live.AssertAllLive(guides);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Endpoint_and_arbitrary_frame_references_share_the_conditioning_chain()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(
            UploadedReference("RklSU1Q=", fromEnd: false),
            UploadedReference("TEFTVA==", fromEnd: true),
            UploadedReference("SUdOT1JF", fromEnd: false, frame: 9));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["height"] = 320);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node[] uploads =
            [.. bridge.Graph.NodesOfType<SwarmLoadImageB64Node>()];
        Assert.Equal(3, uploads.Length);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        SwarmFrameImageNode firstFrame = Assert.IsType<SwarmFrameImageNode>(
            keyframes.FirstFrame.Connection?.Node);
        SwarmFrameImageNode lastFrame = Assert.IsType<SwarmFrameImageNode>(
            keyframes.LastFrame.Connection?.Node);
        Assert.All(
            new[] { firstFrame, lastFrame },
            node =>
            {
                Assert.Equal("fit-green", node.Method.LiteralAsString());
                Assert.Equal(512, node.Width.LiteralAsInt());
                Assert.Equal(320, node.Height.LiteralAsInt());
            });

        // Which upload lands in which slot: two framed images of identical size would otherwise be
        // interchangeable.
        Assert.Equal(
            "RklSU1Q=",
            Assert.IsType<SwarmLoadImageB64Node>(firstFrame.ImagesInput.Connection?.Node)
                .ImageBase64.LiteralAsString());
        Assert.Equal(
            "TEFTVA==",
            Assert.IsType<SwarmLoadImageB64Node>(lastFrame.ImagesInput.Connection?.Node)
                .ImageBase64.LiteralAsString());
        MiniMaxH3AddGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3AddGuideNode>());
        Assert.Equal(9, guide.FrameIdx.LiteralAsInt());
        SwarmFrameImageNode arbitrary = Assert.IsType<SwarmFrameImageNode>(
            guide.Image.Connection?.Node);
        Assert.Equal(
            "SUdOT1JF",
            Assert.IsType<SwarmLoadImageB64Node>(arbitrary.ImagesInput.Connection?.Node)
                .ImageBase64.LiteralAsString());
        Assert.Same(keyframes, guide.PositiveInput.Connection?.Node);
        Assert.Same(guide, StageSampler(bridge, 0).Positive.Connection?.Node);

        live.AssertAllLive([keyframes, guide, firstFrame, lastFrame, arbitrary, .. uploads]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Nothing between the base capture (-4.2) and the refiner capture (5.89) touches CurrentMedia
    /// unless core's refiner (-4) and decode (1) run, so only the production list can tell a
    /// "Refiner" reference from a "Base" one.
    /// </summary>
    [Theory]
    [InlineData("Refiner")]
    [InlineData("Base")]
    public async Task A_host_frame_reference_resolves_to_its_own_stage(string source)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(MakeRef(source));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(clip),
            post =>
            {
                // Core's refiner step is gated on BOTH of these being present.
                post["refinermethod"] = "PostApply";
                post["refinercontrolpercentage"] = 0.2;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode baseSampler = fixture.BaseSampler(bridge);
        SwarmKSamplerNode refinerSampler = fixture.RefinerSampler(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        ComfyNode firstFrame = keyframes.FirstFrame.Connection?.Node;
        Assert.NotNull(firstFrame);
        AssertImageSource(firstFrame, "first_frame");

        Assert.Equal(
            source == "Refiner",
            ReachesUpstream(bridge, firstFrame, refinerSampler.Id));
        Assert.True(ReachesUpstream(bridge, firstFrame, baseSampler.Id));

        live.AssertLive(keyframes);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Text-to-video is the shape where the base capture is H3's own joint audio/video latent
    /// rather than a decoded image, so it is the shape that proves the capture is decoded before
    /// it reaches a keyframe input. Handed over raw, it fails the whole request on a LATENT wired
    /// into an IMAGE — no video, and no warning saying why.
    /// </summary>
    [Fact]
    public async Task A_text_to_video_base_frame_reference_is_decoded_before_it_is_keyframed()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(MakeRef("Base"));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        ComfyNode firstFrame = keyframes.FirstFrame.Connection?.Node;
        Assert.NotNull(firstFrame);
        AssertImageSource(firstFrame, "first_frame");

        live.AssertLive(keyframes);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// StartStep = <c>floor(steps * (1 - control))</c>.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(0.5, 4)]
    [InlineData(0.25, 6)]
    public async Task A_refine_stage_starts_at_the_control_derived_step(
        double control,
        int expectedStartStep)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(), fixture.Stage("PreviousStage", control: control));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode refine = StageSampler(bridge, 1);
        Assert.Equal(expectedStartStep, refine.StartAtStep.LiteralAsInt());
        Assert.Equal(MiniMaxWorkflowFixture.Steps, refine.Steps.LiteralAsInt());

        live.AssertLive(refine);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An Error severity must travel ThrowIfBlocking → SwarmUserErrorException → the API's error
    /// response. Nothing else proves a blocking diagnostic stops the request.
    /// </summary>
    [Fact]
    public async Task A_blocking_diagnostic_rejects_the_request()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };
        clip["audioSource"] = "Upload";
        clip["clipLengthFromAudio"] = true;
        clip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "clip.wav",
        };

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => fixture.GenerateAsync(MakeDocument(clip)));
        Assert.Contains("audio", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Core's image-to-video step (11) builds its own MiniMax graph, which DropCoreOutput (11.05)
    /// must remove before the extension builds the timeline.
    /// </summary>
    [Fact]
    public async Task Image_to_video_drops_the_core_built_graph_and_keeps_one_timeline()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());

        live.AssertLive(latent);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Image_root_uses_explicit_timeline_dimensions_instead_of_host_media_dimensions()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject post = fixture.ImageToVideoPost(
            MakeRootConfig(512, 832, clip),
            request =>
            {
                request["width"] = 832;
                request["height"] = 1216;
            });
        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(post);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Equal(512, latent.Width.LiteralAsInt());
        Assert.Equal(832, latent.Height.LiteralAsInt());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(832, generator.CurrentMedia.Height);
        live.AssertLive(latent);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The base capture (-4.2) precedes core's decode (1), so it is a latent; it must reach the
    /// keyframe chain through a VAE decode, not raw.
    /// </summary>
    [Fact]
    public async Task Base_frame_reference_never_feeds_a_latent_into_the_keyframe_chain()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(MakeRef("Base"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        Assert.NotNull(keyframes.FirstFrame.Connection);
        Assert.Contains(
            bridge.Graph.NodesOfType<IVaeDecode>(),
            decode => ReachesUpstream(bridge, keyframes.FirstFrame.Connection.Node, decode.Id));

        AssertImageSource(keyframes.FirstFrame.Connection?.Node, "first_frame");
        // Only a first-frame reference was authored, so last_frame must be genuinely unwired
        // rather than silently skipped by the image-source check.
        Assert.Null(keyframes.LastFrame.Connection);
        foreach (SwarmFrameImageNode framed in bridge.Graph.NodesOfType<SwarmFrameImageNode>())
        {
            AssertImageSource(framed.ImagesInput.Connection?.Node, "SwarmFrameImage.images");
        }

        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// SwarmMiniMaxH3AddKeyframes unbinds its latent into H3's (video, audio) pair and reads
    /// <c>shape[4]</c> for the canvas width. The video half alone unbinds to four dimensions, so a
    /// stage handing over anything but the joint latent it actually samples dies mid-run on
    /// "tuple index out of range" — the graph builds and ships clean either way.
    /// </summary>
    private static void AssertKeyframesReadTheSampledJointLatent(params SwarmKSamplerNode[] samplers) =>
        Assert.All(
            samplers,
            sampler => Assert.Same(
                sampler.LatentImage.Connection,
                Assert.IsType<SwarmMiniMaxH3AddKeyframesNode>(sampler.Positive.Connection?.Node)
                    .Latent.Connection));
}
