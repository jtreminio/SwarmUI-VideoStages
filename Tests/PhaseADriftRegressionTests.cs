using System.Runtime.CompilerServices;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class PhaseADriftRegressionTests
{
    [Fact]
    public void Ltx_custom_nodes_all_require_the_ltxvideo_feature()
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        string[] nodeClasses =
        [
            LTXVSetAudioRefTokensNode.ClassType,
            LTXVSetVideoLatentNoiseMasksNode.ClassType,
            LTXVSetAudioVideoMaskByTimeNode.ClassType,
        ];

        Assert.All(
            nodeClasses,
            nodeClass => Assert.Equal(
                Ltx2HostIntegration.FeatureFlag,
                ComfyUIBackendExtension.NodeToFeatureMap[nodeClass]));
    }

    [Fact]
    public void ControlNet_capture_skips_image_only_apply_and_recognizes_later_anima_video_apply()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        T2IModelHandler handler = new() { ModelType = "ControlNet" };
        T2IModel model = TestStubModel.Create(handler, "UnitTest_Anima_ControlNet.safetensors");
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, model);
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            ModelFolderFormat = "/",
            Workflow = new JObject(),
        };

        using WorkflowBridge bridge = BridgeSync.For(generator);
        ModelPatchLoaderNode loader = bridge.AddNode(
            new ModelPatchLoaderNode().With(Name: model.ToString(generator.ModelFolderFormat)),
            "1");
        UnknownNode image = bridge.AddStub("UnitTestImage", "2").WithOutputs(WGNodeData.DT_IMAGE);
        UnknownNode imageApply = bridge.AddStub("AnimaLLLiteApply", "3")
            .WithOutputs(WGNodeData.DT_MODEL);
        imageApply.GetInput("model_patch").ConnectToUntyped(loader.MODELPATCH);
        imageApply.GetInput("image").ConnectToUntyped(image.GetOutput(0));
        GetVideoComponentsNode video = bridge.AddNode(new GetVideoComponentsNode(), "4");
        UnknownNode videoApply = bridge.AddStub("AnimaLLLiteApply", "5")
            .WithOutputs(WGNodeData.DT_MODEL);
        videoApply.GetInput("model_patch").ConnectToUntyped(loader.MODELPATCH);
        videoApply.GetInput("image").ConnectToUntyped(video.Images);

        ControlNetCoreMediaCapture capture = new(generator);
        capture.Capture();

        Assert.True(capture.TryGetCapturedControlImage(0, out WGNodeData controlImage));
        Assert.Equal(WorkflowBridge.ToPath(video.Images), controlImage.Path);
        Assert.True(capture.TryGetCapturedApplyImageInput(bridge, 0, out JArray applyImage));
        Assert.Equal(WorkflowBridge.ToPath(video.Images), applyImage);
    }

    [Fact]
    public void Frontend_ic_lora_strength_bounds_match_the_backend()
    {
        string source = File.ReadAllText(Path.GetFullPath(
            Path.Combine(TestSourceDirectory(), "..", "frontend", "icLoraAuthoring.ts")));

        Assert.Contains(
            $"export const IC_LORA_STRENGTH_MIN = {Loras.IcLoraStrengthMin};",
            source);
        Assert.Contains(
            $"export const IC_LORA_STRENGTH_MAX = {Loras.IcLoraStrengthMax};",
            source);
    }

    private static string TestSourceDirectory(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
