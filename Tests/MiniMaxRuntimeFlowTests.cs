using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2;
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
    public void A_second_stage_refines_the_decoded_clip_from_its_own_start_step()
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
                    "Generated",
                    control: 0.5,
                    steps: 8,
                    cfgScale: 1)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, MiniMaxSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(
            [0, 4],
            SamplerNodes(bridge)
                .Select(node => node.FindInput("start_at_step").LiteralAsInt())
                .Order());
        ComfyNode joint = Assert.Single(NodesOfClass(bridge, "LTXVConcatAVLatent"));
        ComfyNode audioMask = Assert.Single(
            NodesOfClass(bridge, "SetLatentNoiseMask"));
        ComfyNode solidMask = Assert.IsType<SolidMaskNode>(
            audioMask.FindInput("mask").Connection?.Node);
        Assert.Equal(0, solidMask.FindInput("value").LiteralAsDouble());
        Assert.Same(
            audioMask,
            joint.FindInput("audio_latent").Connection?.Node);
        ComfyNode refineSampler = Assert.Single(
            SamplerNodes(bridge),
            node => node.FindInput("start_at_step").LiteralAsInt() == 4);
        Assert.Same(
            joint,
            refineSampler.FindInput("latent_image").Connection?.Node);
        Assert.Empty(NodesOfClass(bridge, "VAEEncodeAudio"));
        Assert.Single(NodesOfClass(bridge, "SolidMask"));
        Assert.Single(NodesOfClass(bridge, "VAEDecodeAudio"));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(
            WGNodeData.DT_AUDIO,
            generator.CurrentMedia.AttachedAudio?.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Authored_first_and_last_frame_uploads_reach_the_keyframe_node()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndMiniMaxH3Models();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["refs"] = new JArray(
            UploadedReference("RklSU1Q=", fromEnd: false),
            UploadedReference("TEFTVA==", fromEnd: true));
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
        SwarmLoadImageB64Node first = Assert.IsType<SwarmLoadImageB64Node>(
            keyframes.FindInput("first_frame")?.Connection?.Node);
        SwarmLoadImageB64Node last = Assert.IsType<SwarmLoadImageB64Node>(
            keyframes.FindInput("last_frame")?.Connection?.Node);
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

    private static JObject UploadedReference(string payload, bool fromEnd) =>
        new()
        {
            ["source"] = "Upload",
            ["frame"] = 1,
            ["fromEnd"] = fromEnd,
            ["uploadedImage"] = new JObject
            {
                ["data"] = $"data:image/png;base64,{payload}",
                ["fileName"] = fromEnd ? "last.png" : "first.png",
            },
        };

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> MiniMaxSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<ComfyNode> NodesOfClass(
        WorkflowBridge bridge,
        string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);

    private static IReadOnlyList<ComfyNode> SamplerNodes(WorkflowBridge bridge) =>
        NodesOfClass(bridge, "SwarmKSampler")
            .Concat(NodesOfClass(bridge, "KSamplerAdvanced"))
            .ToArray();
}
