using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.MiniMax;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxRuntimeFlowTests
{
    private static readonly string[] SourceFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    [Fact]
    public void Image_entry_samples_the_joint_latent_and_decodes_native_audio()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode latent = Assert.Single(
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Equal(8, sampler.FindInput("steps").LiteralAsInt());
        Assert.Same(
            latent,
            sampler.FindInput("latent_image").Connection?.Node);
        Assert.NotNull(
            Assert.Single(NodesOfClass(bridge, "SwarmMiniMaxH3AddKeyframes"))
                .FindInput("first_frame")?.Connection);
        Assert.Single(NodesOfClass(bridge, "VAEDecodeAudio"));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(
            WGNodeData.DT_AUDIO,
            generator.CurrentMedia.AttachedAudio?.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Text_entry_samples_an_empty_joint_latent_with_no_keyframes()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                MiniMaxSteps(),
                SourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(NodesOfClass(bridge, "SwarmMiniMaxH3AddKeyframes"));
        ComfyNode latent = Assert.Single(
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.Same(
            latent,
            sampler.FindInput("latent_image").Connection?.Node);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(
            WGNodeData.DT_AUDIO,
            generator.CurrentMedia.AttachedAudio?.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>0.2 s and 1.0 s at 24 fps both snap up to H3's 17k+5 grid.</summary>
    [Fact]
    public void Two_clips_cut_together_into_one_published_video()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject shortClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        shortClip["duration"] = 0.2;
        JObject longClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        longClip["duration"] = 1.0;
        JObject document = MakeDocument(shortClip, longClip);
        document["fps"] = 24;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            document.ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(
            [22, 39],
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV")
                .Select(node => node.FindInput("length").LiteralAsInt())
                .Order());
        Assert.Equal(2, SamplerNodes(bridge).Count());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Two_clips_crossfade_through_the_shared_decoded_merge()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        firstClip["duration"] = 0.2;
        firstClip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        firstClip["boundaryOutOverlap"] = 8;
        JObject secondClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        secondClip["duration"] = 1.0;
        JObject document = MakeDocument(firstClip, secondClip);
        document["fps"] = 24;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            document.ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, SamplerNodes(bridge).Count());
        ImageCompositeMaskedNode blend = Assert.Single(
            bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        SwarmRampMaskBatchNode ramp = Assert.Single(
            bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(8, ramp.Frames.LiteralAsInt());
        Assert.Same(ramp.Mask, blend.Mask.Connection);
        Assert.Single(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(22 + 39 - 8, generator.CurrentMedia.Frames);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Crossfade_audio_carry_conditions_the_next_clip_from_the_previous_tail()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        firstClip["duration"] = 0.2;
        firstClip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        firstClip["boundaryOutOverlap"] = 8;
        firstClip["boundaryOutCarryAudio"] = true;
        JObject secondClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        secondClip["duration"] = 1.0;
        JObject document = MakeDocument(firstClip, secondClip);
        document["fps"] = 24;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            document.ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmSetAudioMaskWindowsNode carryMask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        JObject window = Assert.IsType<JObject>(Assert.Single(
            JArray.Parse(carryMask.Windows.LiteralAsString())));
        Assert.Equal(0, window.Value<double>("start"));
        Assert.Equal(0.33, window.Value<double>("end"), 6);

        TrimAudioDurationNode carryTrim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => node.StartIndex.LiteralAsDouble() == (22 - 8) / 24.0
                && node.Duration.LiteralAsDouble() == 8 / 24.0);
        ComfyNode conditionedSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => ReachesUpstream(bridge, sampler, carryMask.Id));
        ComfyNode previousSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => !ReferenceEquals(sampler, conditionedSampler));
        Assert.True(ReachesUpstream(
            bridge,
            carryTrim.Audio.Connection!.Node,
            previousSampler.Id));
        Assert.True(ReachesUpstream(
            bridge,
            carryMask.Samples.Connection!.Node,
            carryTrim.Id));
        Assert.Single(bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        Assert.NotEmpty(bridge.Graph.NodesOfType<AudioConcatNode>());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Init_video_passthrough_keeps_conformed_footage_and_source_audio()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MiniMaxInitVideoClip(
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0,
                steps: 8,
                cfgScale: 1));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                MiniMaxSteps(),
                SourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Empty(SamplerNodes(bridge));
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath((JArray)generator.CurrentMedia.Path)?.Node,
            window.Id));
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath((JArray)generator.CurrentMedia.AttachedAudio.Path)?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Init_video_can_replace_source_audio_with_an_upload()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MiniMaxInitVideoClip(
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                steps: 8,
                cfgScale: 1));
        clip["audioSource"] = Constants.AudioSourceUpload;
        clip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "replacement.wav",
        };
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                MiniMaxSteps(),
                SourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        ComfyNode sampler = Assert.Single(SamplerNodes(bridge));
        Assert.True(ReachesUpstream(bridge, sampler, upload.Id));
        // No cleanup pass sweeps TrimAudioDuration, so the replaced branch is never built.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => node.StartIndex.LiteralAsDouble() == 1);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Reuse_audio_preserves_the_second_stage_output_for_later_stages()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8, cfgScale: 1),
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8, cfgScale: 1),
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8, cfgScale: 1));
        clip["reuseAudio"] = true;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode thirdSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 45);
        ComfyNode fourthSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 46);
        ComfyNode thirdJoint = thirdSampler.FindInput("latent_image").Connection?.Node;
        ComfyNode fourthJoint = fourthSampler.FindInput("latent_image").Connection?.Node;
        ComfyNode thirdMask = thirdJoint?.FindInput("audio_latent").Connection?.Node;
        ComfyNode fourthMask = fourthJoint?.FindInput("audio_latent").Connection?.Node;
        INodeOutput capturedAudio = thirdMask?.FindInput("samples").Connection;
        Assert.NotNull(capturedAudio);
        Assert.Same(capturedAudio, fourthMask?.FindInput("samples").Connection);
        VAEDecodeAudioNode decode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
        Assert.Same(capturedAudio, decode.Samples.Connection);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Pixel_upscale_resizes_the_decoded_input_before_the_next_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    upscale: 1.5,
                    upscaleMethod: "pixel-lanczos",
                    steps: 8,
                    cfgScale: 1)));

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(2, samplers.Length);
        ComfyNode firstSampler = Assert.Single(
            samplers,
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 43);
        ComfyNode secondSampler = Assert.Single(
            samplers,
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 44);
        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            candidate => candidate.Width.LiteralAsInt() == 768
                && candidate.Height.LiteralAsInt() == 768
                && candidate.UpscaleMethod.LiteralAsString() == "lanczos"
                && ReachesUpstream(bridge, candidate, firstSampler.Id));
        ComfyNode joint = secondSampler.FindInput("latent_image").Connection?.Node;
        Assert.True(ReachesUpstream(bridge, joint, scale.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Latent_interpolation_resizes_only_the_video_half_before_the_next_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    upscale: 1.5,
                    upscaleMethod: "latent-bislerp",
                    steps: 8,
                    cfgScale: 1)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(2, samplers.Length);
        ComfyNode firstSampler = Assert.Single(
            samplers,
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 43);
        ComfyNode secondSampler = Assert.Single(
            samplers,
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 44);
        LatentUpscaleByNode scale = Assert.Single(
            bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", scale.UpscaleMethod.LiteralAsString());
        Assert.Equal(1.5, scale.ScaleBy.LiteralAsDouble());
        Assert.True(ReachesUpstream(bridge, scale, firstSampler.Id));
        LTXVConcatAVLatentNode joint = Assert.IsType<LTXVConcatAVLatentNode>(
            secondSampler.FindInput("latent_image").Connection?.Node);
        Assert.Same(scale.LATENT, joint.VideoLatent.Connection);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            candidate => candidate.Width.LiteralAsInt() == 768
                && candidate.Height.LiteralAsInt() == 768);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Model_upscale_resizes_the_decoded_input_before_the_next_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    upscale: 1.5,
                    upscaleMethod: "model-unit-test-upscaler.pth",
                    steps: 8,
                    cfgScale: 1)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 43);
        ComfyNode secondSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 44);
        ComfyNode loader = Assert.Single(NodesOfClass(bridge, "UpscaleModelLoader"));
        Assert.Equal(
            "unit-test-upscaler.pth",
            loader.FindInput("model_name").LiteralAsString());
        ComfyNode modelUpscale = Assert.Single(
            NodesOfClass(bridge, "ImageUpscaleWithModel"));
        Assert.Same(
            loader,
            modelUpscale.FindInput("upscale_model").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, modelUpscale, firstSampler.Id));
        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            candidate => candidate.Width.LiteralAsInt() == 768
                && candidate.Height.LiteralAsInt() == 768
                && candidate.UpscaleMethod.LiteralAsString() == "lanczos"
                && ReferenceEquals(candidate.Image.Connection?.Node, modelUpscale));
        ComfyNode joint = secondSampler.FindInput("latent_image").Connection?.Node;
        Assert.True(ReachesUpstream(bridge, joint, scale.Id));
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Persisted_and_prompt_LoRAs_are_stage_scoped_and_replace_the_loader_cache()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        AddLoraModel("UnitTest_MiniMax_Prompt.safetensors");
        AddLoraModel("UnitTest_MiniMax_Persisted.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 8,
            cfgScale: 1);
        first["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_MiniMax_Persisted",
            ["weight"] = 0.6,
            ["textEncoderWeight"] = 0.7,
        });
        JObject second = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            steps: 9,
            cfgScale: 1);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second),
            prompt: "global <videoclip[0,0]><lora:UnitTest_MiniMax_Prompt:0.4:0.8>");
        string[] originalLoras = null;
        string[] originalWeights = null;
        string[] originalTextEncoderWeights = null;
        string[] originalConfinements = null;
        WorkflowGenerator.WorkflowGenStep snapshot = new(
            g =>
            {
                originalLoras = [.. g.UserInput.Get(T2IParamTypes.Loras) ?? []];
                originalWeights = [.. g.UserInput.Get(T2IParamTypes.LoraWeights) ?? []];
                originalTextEncoderWeights =
                    [.. g.UserInput.Get(T2IParamTypes.LoraTencWeights) ?? []];
                originalConfinements =
                    [.. g.UserInput.Get(T2IParamTypes.LoraSectionConfinement) ?? []];
            },
            Constants.WorkflowStepPriority.RunConfiguredStages - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                MiniMaxSteps().Append(snapshot));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode firstSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 8);
        ComfyNode secondSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => sampler.FindInput("steps").LiteralAsInt() == 9);
        ComfyNode[] loras = [.. LoraLoaderNodesOf(bridge)];
        Assert.Equal(2, loras.Length);
        Assert.All(
            loras,
            lora =>
            {
                Assert.True(ReachesUpstream(
                    bridge,
                    firstSampler.FindInput("model").Connection?.Node,
                    lora.Id));
                Assert.False(ReachesUpstream(
                    bridge,
                    secondSampler.FindInput("model").Connection?.Node,
                    lora.Id));
            });
        Assert.Contains(
            loras,
            lora => lora.FindInput("lora_name").LiteralAsString()
                == "UnitTest_MiniMax_Prompt.safetensors");
        Assert.Contains(
            loras,
            lora => lora.FindInput("lora_name").LiteralAsString()
                == "UnitTest_MiniMax_Persisted.safetensors");
        Assert.Equal(originalLoras, input.Get(T2IParamTypes.Loras) ?? []);
        Assert.Equal(originalWeights, input.Get(T2IParamTypes.LoraWeights) ?? []);
        Assert.Equal(
            originalTextEncoderWeights,
            input.Get(T2IParamTypes.LoraTencWeights) ?? []);
        Assert.Equal(
            originalConfinements,
            input.Get(T2IParamTypes.LoraSectionConfinement) ?? []);
        string loaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        Assert.True(generator.NodeHelpers.TryGetValue(loaderKey, out string cachedLoader));
        Assert.Equal(
            secondSampler.FindInput("model").Connection?.Node?.Id,
            cachedLoader.Split(':')[0]);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Authored_first_and_last_frame_uploads_use_reference_framing()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["refs"] = new JArray(
            UploadedReference("RklSU1Q=", fromEnd: false),
            UploadedReference("TEFTVA==", fromEnd: true));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            MiniMaxSteps(),
            SourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode keyframes = Assert.Single(
            NodesOfClass(bridge, "SwarmMiniMaxH3AddKeyframes"));
        SwarmFrameImageNode firstFrame = Assert.IsType<SwarmFrameImageNode>(
            keyframes.FindInput("first_frame")?.Connection?.Node);
        SwarmFrameImageNode lastFrame = Assert.IsType<SwarmFrameImageNode>(
            keyframes.FindInput("last_frame")?.Connection?.Node);
        SwarmLoadImageB64Node first = Assert.IsType<SwarmLoadImageB64Node>(
            firstFrame.ImagesInput.Connection?.Node);
        SwarmLoadImageB64Node last = Assert.IsType<SwarmLoadImageB64Node>(
            lastFrame.ImagesInput.Connection?.Node);
        ComfyNode latent = Assert.Single(NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        Assert.Equal("fit-green", firstFrame.Method.LiteralAsString());
        Assert.Equal("fit-green", lastFrame.Method.LiteralAsString());
        Assert.Equal(latent.FindInput("width").LiteralAsInt(), firstFrame.Width.LiteralAsInt());
        Assert.Equal(latent.FindInput("height").LiteralAsInt(), firstFrame.Height.LiteralAsInt());
        Assert.Equal(firstFrame.Width.LiteralAsInt(), lastFrame.Width.LiteralAsInt());
        Assert.Equal(firstFrame.Height.LiteralAsInt(), lastFrame.Height.LiteralAsInt());
        Assert.Equal(
            "RklSU1Q=",
            first.ImageBase64.LiteralAsString());
        Assert.Equal(
            "TEFTVA==",
            last.ImageBase64.LiteralAsString());
        Assert.Same(
            keyframes,
            Assert.Single(SamplerNodes(bridge)).FindInput("positive").Connection?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Final_frame_reference_is_reframed_for_each_stage_resolution()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: "pixel-lanczos",
                steps: 8,
                cfgScale: 1));
        clip["refs"] = new JArray(UploadedReference("TEFTVA==", fromEnd: true));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            MiniMaxSteps(),
            SourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        SwarmFrameImageNode[] framed =
            [.. bridge.Graph.NodesOfType<SwarmFrameImageNode>()
                .Where(node => ReferenceEquals(node.ImagesInput.Connection?.Node, upload))];
        Assert.Equal([512, 768], framed.Select(node => node.Width.LiteralAsInt()).Order());
        Assert.All(framed, node =>
        {
            Assert.Equal("fit-green", node.Method.LiteralAsString());
            Assert.Same(upload, node.ImagesInput.Connection?.Node);
        });
        Assert.Equal(
            [512, 768],
            NodesOfClass(bridge, "SwarmMiniMaxH3AddKeyframes")
                .Select(node => Assert.IsType<SwarmFrameImageNode>(
                    node.FindInput("last_frame")?.Connection?.Node))
                .Select(node => node.Width.LiteralAsInt())
                .Order());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Refiner_first_and_last_frame_references_reach_the_keyframe_node()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["refs"] = new JArray(
            MakeRef("Refiner"),
            MakeRef("Refiner", fromEnd: true));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            MiniMaxSteps(),
            SourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode keyframes = Assert.Single(
            NodesOfClass(bridge, "SwarmMiniMaxH3AddKeyframes"));
        Assert.NotNull(keyframes.FindInput("first_frame")?.Connection);
        Assert.NotNull(keyframes.FindInput("last_frame")?.Connection);
        Assert.Same(
            keyframes.FindInput("first_frame").Connection,
            keyframes.FindInput("last_frame").Connection);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Uploaded_audio_is_preserved_in_the_entry_joint_latent()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["audioSource"] = Constants.AudioSourceUpload;
        clip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "clip.wav",
        };
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        VAEEncodeAudioNode encode = Assert.Single(
            bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection!.Node, upload.Id));
        SetLatentNoiseMaskNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(mask.LATENT, joint.AudioLatent.Connection);
        Assert.Same(
            joint,
            Assert.Single(SamplerNodes(bridge)).FindInput("latent_image").Connection?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Uploaded_audio_can_drive_the_entry_joint_latent_length()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["duration"] = 1.0;
        clip["audioSource"] = Constants.AudioSourceUpload;
        clip["clipLengthFromAudio"] = true;
        clip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "clip.wav",
        };
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Equal(24, lengthToFrames.FrameRate.LiteralAsInt());
        Assert.Equal(17, lengthToFrames.FrameGrid.LiteralAsInt());
        Assert.Equal(5, lengthToFrames.FrameGridOrigin.LiteralAsInt());
        Assert.Equal(0, lengthToFrames.FrameCountOffset.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, lengthToFrames.AudioInput.Connection!.Node, upload.Id));
        ComfyNode emptyJoint = Assert.Single(
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        Assert.Same(
            lengthToFrames.Frames,
            emptyJoint.FindInput("length").Connection);
        VAEEncodeAudioNode encode = Assert.Single(
            bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection!.Node, lengthToFrames.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void ControlNet_audio_can_drive_the_entry_joint_latent_length()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["audioSource"] = Constants.AudioSourceControlNet;
        clip["clipLengthFromAudio"] = true;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                MiniMaxSteps().Append(SeedControlNetAudioTracksStep(1)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        GetVideoComponentsNode source = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Same(source.Audio, lengthToFrames.AudioInput.Connection);
        ComfyNode emptyJoint = Assert.Single(
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        Assert.Same(
            lengthToFrames.Frames,
            emptyJoint.FindInput("length").Connection);
        VAEEncodeAudioNode encode = Assert.Single(
            bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode, source.Id));
        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(
            joint,
            Assert.Single(SamplerNodes(bridge)).FindInput("latent_image").Connection?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Unusable_ControlNet_audio_warns_and_keeps_native_H3_audio_generation(
        int capturedTracks)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["audioSource"] = Constants.AudioSourceControlNet;
        clip["clipLengthFromAudio"] = true;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps = capturedTracks == 0
            ? MiniMaxSteps()
            : MiniMaxSteps().Append(SeedControlNetAudioTracksStep(capturedTracks));
        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(
            capturedTracks,
            bridge.Graph.NodesOfType<GetVideoComponentsNode>().Count());
        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Contains(
            warnings,
            warning => warning.Contains("ControlNet audio")
                && warning.Contains("using silence"));
        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        ComfyNode emptyJoint = Assert.Single(
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        Assert.Same(
            emptyJoint,
            Assert.Single(SamplerNodes(bridge)).FindInput("latent_image").Connection?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Audio_derived_duration_refuses_multi_clip_and_global_trim_requests()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject dynamicClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        dynamicClip["audioSource"] = Constants.AudioSourceUpload;
        dynamicClip["clipLengthFromAudio"] = true;
        dynamicClip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QUJD",
            ["fileName"] = "clip.wav",
        };
        JObject fixedClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(dynamicClip, fixedClip).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 1);
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Workflow = [],
        };
        VideoExecutionPlan plan = generator.RequireVideoExecutionPlanContext().Plan;

        IReadOnlyList<PlanDiagnostic> diagnostics = new MiniMaxExecutionAdapter(generator)
            .PreflightRequest(new(plan, MiniMaxArchitectureModule.ArchitectureId));

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code
                == "minimax.audio-derived-duration.multi-clip-unsupported");
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code
                == "minimax.audio-derived-duration.trim-unsupported");
        Assert.All(
            diagnostics.Where(diagnostic => diagnostic.Code.StartsWith(
                "minimax.audio-derived-duration",
                StringComparison.Ordinal)),
            diagnostic => Assert.Equal(PlanDiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public void AceStepFun_audio_can_drive_the_entry_joint_latent_length()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["audioSource"] = "audio0";
        clip["clipLengthFromAudio"] = true;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                MiniMaxSteps().Append(SeedAceStepFunAudioTrackStep(0)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        string aceDecodeId = AudioHandler.MakeAceStepFunDecodeId(0);
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.True(ReachesUpstream(
            bridge,
            lengthToFrames.AudioInput.Connection!.Node,
            aceDecodeId));
        ComfyNode emptyJoint = Assert.Single(
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        Assert.Same(
            lengthToFrames.Frames,
            emptyJoint.FindInput("length").Connection);
        VAEEncodeAudioNode encode = Assert.Single(
            bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection!.Node, aceDecodeId));
        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(
            joint,
            Assert.Single(SamplerNodes(bridge)).FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, joint, aceDecodeId));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Missing_AceStepFun_audio_warns_and_keeps_native_H3_audio_generation()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["audioSource"] = "audio7";
        clip["clipLengthFromAudio"] = true;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Contains(
            warnings,
            warning => warning.Contains("audio7")
                && warning.Contains("continuing without that source"));
        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        ComfyNode emptyJoint = Assert.Single(
            NodesOfClass(bridge, "EmptyMiniMaxH3LatentAV"));
        Assert.NotNull(emptyJoint.FindInput("length").LiteralAsInt());
        Assert.Same(
            emptyJoint,
            Assert.Single(SamplerNodes(bridge)).FindInput("latent_image").Connection?.Node);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Timeline_audio_uses_aligned_clip_windows_and_reaches_the_entry_joint_latent()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        firstClip["duration"] = 1.0;
        JObject secondClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        secondClip["duration"] = 1.0;
        JObject document = MakeDocument(firstClip, secondClip);
        document["fps"] = 24;
        document["audioTracks"] = new JArray(new JObject
        {
            ["id"] = "timeline-segment",
            ["volume"] = 0.5,
            ["source"] = new JObject
            {
                ["kind"] = "Upload",
                ["reference"] = "timeline.wav",
                ["uploadedAudio"] = new JObject
                {
                    ["data"] = "data:audio/wav;base64,QUJD",
                    ["fileName"] = "timeline.wav",
                },
            },
            ["spans"] = new JArray(new JObject
            {
                ["timelineStartSeconds"] = 39.0 / 24.0 + 0.1,
                ["timelineLengthSeconds"] = 0.2,
                ["sourceStartSeconds"] = 0.1,
            }),
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            document.ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(0.1, trim.StartIndex.LiteralAsDouble());
        Assert.Equal(0.2, trim.Duration.LiteralAsDouble().Value, 8);
        double?[] silenceDurations = bridge.Graph.NodesOfType<EmptyAudioNode>()
            .Select(node => node.Duration.LiteralAsDouble())
            .Order()
            .ToArray();
        Assert.Equal(2, silenceDurations.Length);
        Assert.Equal(0.1, silenceDurations[0].Value, 8);
        Assert.Equal(39.0 / 24.0, silenceDurations[1].Value, 8);
        VAEEncodeAudioNode encode = Assert.Single(
            bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        Assert.True(ReachesUpstream(bridge, encode.Audio.Connection!.Node, upload.Id));
        Assert.Empty(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        SwarmSetAudioMaskWindowsNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        Assert.Equal(1.0, mask.GapMaskValue.LiteralAsDouble());
        JArray windows = JArray.Parse(mask.Windows.LiteralAsString());
        JObject window = Assert.IsType<JObject>(Assert.Single(windows));
        Assert.Equal(0.1, window.Value<double>("start"));
        Assert.Equal(0.3, window.Value<double>("end"));
        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(mask.Latent, joint.AudioLatent.Connection);
        ComfyNode conditionedSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => ReferenceEquals(
                joint,
                sampler.FindInput("latent_image").Connection?.Node));
        ComfyNode unconditionedSampler = Assert.Single(
            SamplerNodes(bridge),
            sampler => !ReferenceEquals(
                joint,
                sampler.FindInput("latent_image").Connection?.Node));
        Assert.True(ReachesUpstream(bridge, conditionedSampler, upload.Id));
        Assert.False(ReachesUpstream(bridge, unconditionedSampler, upload.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    private static JObject MiniMaxInitVideoClip(params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };
        return clip;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> MiniMaxSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedAceStepFunAudioTrackStep(
        int trackIndex) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            bridge.AddNode(
                new VAEDecodeAudioNode(),
                AudioHandler.MakeAceStepFunDecodeId(trackIndex));
        }, 11.05);

    private static WorkflowGenerator.WorkflowGenStep SeedControlNetAudioTracksStep(
        int count) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            for (int index = 0; index < count; index++)
            {
                string nodeId = $"90{index + 1}";
                bridge.AddNode(new GetVideoComponentsNode(), nodeId);
                g.NodeHelpers[ControlNetCaptureKeys.Audio(index)] =
                    new JArray(nodeId, 1).ToString(Newtonsoft.Json.Formatting.None);
            }
        }, 11.05);

    private static IEnumerable<ComfyNode> NodesOfClass(
        WorkflowBridge bridge,
        string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);

    private static IReadOnlyList<ComfyNode> SamplerNodes(WorkflowBridge bridge) =>
        NodesOfClass(bridge, "SwarmKSampler")
            .Concat(NodesOfClass(bridge, "KSamplerAdvanced"))
            .ToArray();

    private static void EnableHostLoraLoading()
    {
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(
                T2IParamInput.SectionID_Video,
                g.LoadingModel,
                g.LoadingClip);
        }, -10);
    }

    private static void AddLoraModel(string name)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            handler = new() { ModelType = "LoRA" };
            Program.T2IModelSets["LoRA"] = handler;
        }
        T2IModel model = TestStubModel.Create(handler, name);
        handler.Models[model.Name] = model;
    }
}
