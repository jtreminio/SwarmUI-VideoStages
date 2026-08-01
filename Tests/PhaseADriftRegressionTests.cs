using System.Runtime.CompilerServices;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
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
            LTXVLaplacianPyramidBlendNode.ClassType,
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
    public void ControlNet_discovery_recognizes_anima_model_patch_apply()
    {
        T2IModelHandler handler = new() { ModelType = "ControlNet" };
        T2IModel model = new(
            handler,
            "/tmp",
            "/tmp/UnitTest_Anima_ControlNet.safetensors",
            "UnitTest_Anima_ControlNet.safetensors");
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            ModelFolderFormat = "/",
            Workflow = new JObject(),
        };

        using WorkflowBridge bridge = BridgeSync.For(generator);
        ModelPatchLoaderNode loader = bridge.AddNode(
            new ModelPatchLoaderNode().With(Name: model.ToString(generator.ModelFolderFormat)),
            "1");
        UnknownNode image = bridge.AddStub("UnitTestImage", "2").WithOutputs(WGNodeData.DT_IMAGE);
        UnknownNode apply = bridge.AddStub("AnimaLLLiteApply", "3").WithOutputs(WGNodeData.DT_MODEL);
        apply.GetInput("model_patch").ConnectToUntyped(loader.MODELPATCH);
        apply.GetInput("image").ConnectToUntyped(image.GetOutput(0));

        bool found = new ControlNetGraphDiscovery(generator).TryFindCoreApply(
            bridge,
            model,
            new HashSet<string>(),
            out (string Id, JObject Node) applyNode,
            out JArray controlImage);

        Assert.True(found);
        Assert.Equal("3", applyNode.Id);
        Assert.Equal(new JArray("2", 0), controlImage);
    }

    [Fact]
    public void Frontend_ic_lora_strength_bounds_match_the_backend()
    {
        string source = File.ReadAllText(Path.GetFullPath(
            Path.Combine(TestSourceDirectory(), "..", "frontend", "icLoraAuthoring.ts")));

        Assert.Contains(
            $"export const IC_LORA_STRENGTH_MIN = {VideoStageResourceParser.IcLoraStrengthMin};",
            source);
        Assert.Contains(
            $"export const IC_LORA_STRENGTH_MAX = {VideoStageResourceParser.IcLoraStrengthMax};",
            source);
    }

    private static string TestSourceDirectory(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
