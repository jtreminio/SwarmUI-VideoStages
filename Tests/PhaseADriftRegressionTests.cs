using System.Globalization;
using System.Text.RegularExpressions;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using VideoStages.Execution.Graph;
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

        // Set equality, not a spot check: a node registered without the flag and a new
        // registration missing from this list both have to fail.
        Assert.Equal(
            [
                LTXAddVideoICLoRAGuideNode.ClassType,
                LTXAddVideoICLoRAGuideAdvancedNode.ClassType,
                LTXICLoRALoaderModelOnlyNode.ClassType,
                LTXVSetAudioRefTokensNode.ClassType,
                LTXVSetAudioVideoMaskByTimeNode.ClassType,
                LTXVSetVideoLatentNoiseMasksNode.ClassType,
            ],
            ComfyUIBackendExtension.NodeToFeatureMap
                .Where(entry => entry.Value == Ltx2HostIntegration.FeatureFlag)
                .Select(entry => entry.Key)
                .OrderBy(nodeClass => nodeClass, StringComparer.Ordinal)
                .ToArray());
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
        string source = RepoFiles.ReadFrontend("icLoraAuthoring.ts");

        Assert.Contains(
            $"export const IC_LORA_STRENGTH_MIN = {Loras.IcLoraStrengthMin};",
            source);
        Assert.Contains(
            $"export const IC_LORA_STRENGTH_MAX = {Loras.IcLoraStrengthMax};",
            source);
    }

    [Fact]
    public void Frontend_stage_ref_strength_default_matches_the_backend()
    {
        Assert.Equal(
            Constants.DefaultStageRefStrength,
            FrontendConstant("STAGE_REF_STRENGTH_DEFAULT"));
    }

    [Fact]
    public void Frontend_audio_span_bounds_match_the_backend()
    {
        Assert.Equal(
            AuthoringTimeline.MinAudioLength,
            FrontendConstant("AUDIO_SPAN_MIN_LENGTH"));
        Assert.Equal(
            AuthoringTimeline.MinAudioVolume,
            FrontendConstant("AUDIO_SPAN_VOLUME_MIN"));
        Assert.Equal(
            AuthoringTimeline.MaxAudioVolume,
            FrontendConstant("AUDIO_SPAN_VOLUME_MAX"));
    }

    /// <summary>Reads a number out of the hand-written frontend module. Parsing it beats matching
    /// the rendered text: C# spells 0.00001 as 1E-05, which is the same number and not drift.
    /// </summary>
    private static double FrontendConstant(string name)
    {
        Match declaration = Regex.Match(
            RepoFiles.ReadFrontend("constants.ts"),
            $"^export const {name} = (\\S+);$",
            RegexOptions.Multiline);
        Assert.True(declaration.Success, $"constants.ts declares no {name}.");
        return double.Parse(declaration.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
