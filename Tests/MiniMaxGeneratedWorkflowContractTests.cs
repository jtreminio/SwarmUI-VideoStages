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

// Identify stages by seed; refinement makes upstream reachability ambiguous.
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
    public async Task Stage_attention_windows_patch_only_their_own_H3_samplers()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, steps: 10),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12));
        clip["duration"] = 1.0;
        clip["stages"][0]["h3AttentionWindowSeconds"] = 2.5;
        clip["stages"][1]["h3AttentionWindowSeconds"] = 4;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraFeatures: [MiniMaxAttentionWindowGraph.FeatureFlag]);
        JProperty[] windows = [.. workflow.Properties().Where(property =>
            property.Value["class_type"]?.Value<string>()
                == H3WindowAttentionPatchNode.ClassType)];
        JProperty[] samplers = [.. workflow.Properties().Where(property =>
            property.Value["class_type"]?.Value<string>() == SwarmKSamplerNode.ClassType)];

        Assert.Equal(2, windows.Length);
        Assert.Equal(3, samplers.Length);
        Assert.All(windows, property =>
        {
            JObject inputs = (JObject)property.Value["inputs"];

            Assert.Equal("0,9,19,29,39,49", inputs["dense_layers"]?.Value<string>());
            Assert.True(inputs["verbose"]?.Value<bool>());
        });
        Assert.All(samplers, sampler =>
        {
            JObject inputs = (JObject)sampler.Value["inputs"];
            string modelId = inputs["model"][0].Value<string>();
            if (inputs["steps"].Value<int>() == 12)
            {
                Assert.DoesNotContain(windows, window => window.Name == modelId);
            }
            else
            {
                Assert.Contains(windows, window => window.Name == modelId);
                Assert.Equal(inputs["steps"].Value<int>() == 8 ? 2.5 : 4,
                    workflow[modelId]["inputs"]["window_seconds"].Value<double>());
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stage_attention_window_applies_to_audio_only_and_Bonita(bool audioOnly)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        using IDisposable yolo = audioOnly ? null : BonitaTestModels.InstallYoloModels();
        JObject stage = fixture.Stage(audioOnly ? null : "PreviousStage", control: 0.5);
        stage["h3AttentionWindowSeconds"] = 3.5;
        if (!audioOnly) stage["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject clip = audioOnly ? MakeClip(stage) : MakeClip(fixture.Stage(), stage);
        clip["duration"] = 1.0;
        clip["audioOnly"] = audioOnly;
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraFeatures: [MiniMaxAttentionWindowGraph.FeatureFlag, .. BonitaTestModels.Features()]);
        JProperty window = Assert.Single(workflow.Properties(), property =>
            property.Value["class_type"]?.Value<string>() == H3WindowAttentionPatchNode.ClassType);
        Assert.Equal(3.5, window.Value["inputs"]["window_seconds"].Value<double>());
        Assert.Contains(workflow.Properties(), property =>
            property.Value["class_type"]?.Value<string>() == SwarmKSamplerNode.ClassType
            && property.Value["inputs"]?["model"]?[0]?.Value<string>() == window.Name);
    }

    [Fact]
    public async Task Stage_attention_window_stays_off_at_zero_or_without_JuanAttn()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["stages"][0]["h3AttentionWindowSeconds"] = 2.5;

        JObject withoutFeature = await fixture.GenerateAsync(MakeDocument(clip));
        clip["stages"][0]["h3AttentionWindowSeconds"] = 0;
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
        SwarmEmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<SwarmEmptyMiniMaxH3LatentAVNode>());
        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        VAEDecodeAudioNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeAudioNode>());

        Assert.Same(latent, sampler.LatentImage.Connection?.Node);
        Assert.Equal(MiniMaxWorkflowFixture.Steps, sampler.Steps.LiteralAsInt());

        Assert.Equal(39, latent.Length.LiteralAsInt());

        live.AssertAllLive(latent, sampler, audioDecode);
        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audio_only_samples_a_32_pixel_joint_latent_and_publishes_only_audio(bool websocketAudio)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioOnly"] = true;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)),
                extraFeatures: websocketAudio ? ["comfy_saveaudio_ws"] : []);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmEmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<SwarmEmptyMiniMaxH3LatentAVNode>());
        Assert.Equal(32, latent.Width.LiteralAsInt());
        Assert.Equal(32, latent.Height.LiteralAsInt());
        Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>());
        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
        ComfyNode save = Assert.Single(bridge.Graph.Nodes.Values, node =>
            node.ClassTypeName.Contains("SaveAudio", StringComparison.Ordinal));
        Assert.Equal(websocketAudio ? "SwarmSaveAudioWS" : "SaveAudioAdvanced", save.ClassTypeName);
        JObject inputs = (JObject)workflow[save.Id]["inputs"];
        Assert.Equal("mp3", websocketAudio
            ? inputs["format"].Value<string>()
            : inputs["format"]["format"].Value<string>());
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, inputs["audio"]));
        AssertNoDanglingNodeRefs(workflow);
        Assert.Empty(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.DoesNotContain(workflow.Properties(), property =>
            property.Value["class_type"]?.Value<string>() is string type
            && type.Contains("VAEDecode", StringComparison.Ordinal)
            && !type.Contains("Audio", StringComparison.Ordinal));
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.DataType);
    }

    [Fact]
    public async Task A_later_video_clip_can_consume_the_audio_only_clip_output()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject audio = MakeClip(fixture.Stage());
        audio["duration"] = 1.0;
        audio["audioOnly"] = true;
        JObject video = MakeClip(fixture.Stage());
        video["duration"] = 1.0;
        video["audioSource"] = MediaSource.PreviousClip;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(audio, video));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmKSamplerNode audioSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode videoSampler = StageSampler(bridge, 1);
        VAEDecodeAudioNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeAudioNode>(),
            decode => decode.Samples.Connection?.Node is LTXVSeparateAVLatentNode separate
                && ReferenceEquals(separate.AvLatent.Connection?.Node, audioSampler));
        LTXVConcatAVLatentNode videoInput = Assert.IsType<LTXVConcatAVLatentNode>(
            videoSampler.LatentImage.Connection?.Node);

        Assert.True(ReachesUpstream(
            bridge,
            videoInput.AudioLatent.Connection?.Node,
            audioDecode.Id));
        Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.DoesNotContain(bridge.Graph.Nodes.Values, node =>
            node.ClassTypeName.Contains("SaveAudio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audio_only_lead_overrides_the_next_clips_native_audio()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject audio = MakeClip(fixture.Stage());
        audio["duration"] = 1.0;
        audio["audioOnly"] = true;
        JObject video = MakeClip(fixture.Stage());
        video["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(audio, video));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmKSamplerNode audioSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode videoSampler = StageSampler(bridge, 1);
        VAEDecodeAudioNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeAudioNode>(),
            decode => decode.Samples.Connection?.Node is LTXVSeparateAVLatentNode separate
                && ReferenceEquals(separate.AvLatent.Connection?.Node, audioSampler));
        LTXVConcatAVLatentNode videoInput = Assert.IsType<LTXVConcatAVLatentNode>(
            videoSampler.LatentImage.Connection?.Node);

        Assert.Empty(bridge.Graph.NodesOfType<AudioMergeNode>());
        Assert.True(ReachesUpstream(
            bridge,
            videoInput.AudioLatent.Connection?.Node,
            audioDecode.Id));
        Assert.DoesNotContain(bridge.Graph.Nodes.Values, node =>
            node.ClassTypeName.Contains("SaveAudio", StringComparison.Ordinal));
        Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
    }

    // Re-encoding collapses onto the sampler latent; audio must survive pruning the unused decode.
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

    // Core's save, image-to-video step, and the extension can each emit a trim.
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

    [Theory]
    [InlineData(0.6)]
    [InlineData(-0.3)]
    [InlineData(0)]
    public async Task Persisted_stage_LoRA_strengths_and_prompt_LoRAs_stay_scoped(double secondStrength)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_MiniMax_Prompt.safetensors")
            .InstallModel("LoRA", "UnitTest_MiniMax_Persisted.safetensors");

        JObject first = fixture.Stage();
        JObject clip = MakeClip(first, fixture.Stage("PreviousStage", control: 0.5, steps: 9));
        foreach (JObject authoredStage in clip["stages"])
        {
            authoredStage["loras"] = new JArray(new JObject
            {
                ["name"] = "UnitTest_MiniMax_Persisted",
                ["weight"] = 0.6,
                ["textEncoderWeight"] = 0.7,
            });
        }
        clip["stages"][1]["loras"][0]["weight"] = secondStrength;
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
        ComfyNode firstLora = Assert.Single(persisted,
            lora => ReachesUpstream(bridge, firstSampler.Model.Connection?.Node, lora.Id));
        Assert.Equal(0.6, firstLora.FindInput("strength_model").LiteralAsDouble());
        ComfyNode[] secondLoras = [.. persisted.Where(
            lora => ReachesUpstream(bridge, secondSampler.Model.Connection?.Node, lora.Id))];
        if (secondStrength == 0)
        {
            Assert.Empty(secondLoras);
        }
        else
        {
            Assert.Equal(secondStrength,
                Assert.Single(secondLoras).FindInput("strength_model").LiteralAsDouble());
        }

        if (secondStrength != 0) Assert.DoesNotContain(
            $"modelloader_{fixture.Model.Name}_image2video",
            generator.NodeHelpers.Keys);

        live.AssertAllLive([firstSampler, secondSampler, .. loras]);
        AssertShippable(bridge, workflow, live);
    }

    // Framing reuse depends on source, size, and method.
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

        // Reachability alone cannot distinguish the first-frame and last-frame slots.
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

    // Refinement must keep the authored opening image at the new stage resolution.
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

        // Equal dimensions cannot distinguish which upload reached each slot.
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

    // Run core's refiner and decode to distinguish the Base and Refiner capture points.
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
                // Core requires both refiner parameters.
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

    [Theory]
    [InlineData("Base", 0, false)]
    [InlineData("Refiner", 0, false)]
    [InlineData("Base", 0, true)]
    [InlineData("Refiner", 0, true)]
    [InlineData("Base", 9, false)]
    [InlineData("Refiner", 9, false)]
    public async Task A_text_to_video_keyframe_cannot_reference_the_replaced_host_root(
        string source,
        int frame,
        bool fromEnd)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(MakeRef(source, frame: frame, fromEnd: fromEnd));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(StageSampler(bridge, 0), sampler);
        Assert.Empty(bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<MiniMaxH3AddGuideNode>());

        live.AssertLive(sampler);
        AssertShippable(bridge, workflow, live);
    }

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

    // Exercise the API conversion from a blocking diagnostic to a readable error.
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

    // DropCoreOutput must remove core's video graph before the timeline runs.
    [Fact]
    public async Task Image_to_video_drops_the_core_built_graph_and_keeps_one_timeline()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmEmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<SwarmEmptyMiniMaxH3LatentAVNode>());
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

        SwarmEmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<SwarmEmptyMiniMaxH3LatentAVNode>());
        Assert.Equal(512, latent.Width.LiteralAsInt());
        Assert.Equal(832, latent.Height.LiteralAsInt());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(832, generator.CurrentMedia.Height);
        live.AssertLive(latent);
        AssertShippable(bridge, workflow, live);
    }

    // Base is captured before decode, so its reference needs a VAE conversion.
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
        Assert.Null(keyframes.LastFrame.Connection);
        foreach (SwarmFrameImageNode framed in bridge.Graph.NodesOfType<SwarmFrameImageNode>())
        {
            AssertImageSource(framed.ImagesInput.Connection?.Node, "SwarmFrameImage.images");
        }

        AssertShippable(bridge, workflow, live);
    }

    // The keyframe node requires the joint (video, audio) latent and reads video.shape[4].
    private static void AssertKeyframesReadTheSampledJointLatent(params SwarmKSamplerNode[] samplers) =>
        Assert.All(
            samplers,
            sampler => Assert.Same(
                sampler.LatentImage.Connection,
                Assert.IsType<SwarmMiniMaxH3AddKeyframesNode>(sampler.Positive.Connection?.Node)
                    .Latent.Connection));
}
