using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class StageRefStoreScopeTests
{
    // Covers key families that a representative generated workflow does not reach.
    [Fact]
    public void Every_owned_runtime_key_family_uses_the_ltx_architecture_prefix()
    {
        LtxRuntimeKeyScope keys = new();
        List<string> runtimeKeys =
        [
            keys.ControlNetNormalized,
            keys.ControlNetFullImage(2),
            keys.ControlNetFrameCount(2),
            keys.IcLoraAudioReference(3, 4),
            keys.IcLoraAudioReference(3, 4, 5),
            keys.IcLoraControlSignal(3, 4),
            keys.IcLoraUploadedDriveImages(3, 4),
        ];
        runtimeKeys.AddRange(
            from StageRefStore.StageKind kind in Enum.GetValues<StageRefStore.StageKind>()
            from LtxRuntimeKeyScope.StageRefComponent component
                in Enum.GetValues<LtxRuntimeKeyScope.StageRefComponent>()
            select keys.StageRef(kind, component));

        Assert.All(
            runtimeKeys,
            key => Assert.StartsWith("videostages.arch.ltx2.", key));
        Assert.Equal(runtimeKeys.Count, runtimeKeys.Distinct().Count());
    }

    [Fact]
    public void A_generated_ltx_workflow_writes_no_unscoped_runtime_key()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");
        JObject clip = Fixtures.MakeClip(
            Fixtures.MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            Fixtures.MakeStage(models.VideoModel.Name, "Generated", upscale: 2, steps: 10));
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "UnitTest_IcLoraA",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["driveData"] = $"{IcLoraDriveData.Visual}",
            ["strength"] = 1.0,
            ["attentionStrength"] = 1.0,
            ["controlType"] = Constants.IcLoraControlCanny,
            ["driveMedia"] = new JObject
            {
                ["data"] = "data:video/mp4;base64,QUJD",
                ["fileName"] = "drive.mp4",
            },
        });

        (JObject _, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                Fixtures.BuildNativeInput(
                    models.BaseModel,
                    models.VideoModel,
                    new JArray(clip).ToString()),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));

        // Common orchestration owns the architecture-neutral ControlNet captures.
        string[] videoStagesKeys = [.. generator.NodeHelpers.Keys
            .Where(key => key.StartsWith("videostages.", StringComparison.Ordinal))
            .Where(key => !key.StartsWith("videostages.controlnet.", StringComparison.Ordinal))];

        Assert.NotEmpty(videoStagesKeys);
        Assert.All(
            videoStagesKeys,
            key => Assert.StartsWith("videostages.arch.ltx2.", key));
    }

    [Fact]
    public void Ltx_scope_interoperates_across_store_instances()
    {
        WorkflowGenerator generator = Generator();
        StageRefStore capture = new(generator);
        StageRefStore read = new(generator);
        (WGNodeData Media, WGNodeData Vae) data =
            AddStageData(generator, "300");

        capture.Capture(
            StageRefStore.StageKind.Generated,
            data.Media,
            data.Vae);

        AssertStageRef(read.Generated, "300", "301", "302");
    }

    private static WorkflowGenerator.WorkflowGenStep SeedRefinerImageStep() =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            UnknownNode refinerImage = bridge.AddStub("UnitTest_RefinerImage", "200")
                .WithOutputs(WGNodeData.DT_IMAGE);
            g.CurrentMedia = refinerImage.GetOutput(0).ToWGMedia(
                g,
                WGNodeData.DT_IMAGE,
                width: 512,
                height: 512);
        }, 4.0);

    private static void RegisterLora(string name)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            handler = new T2IModelHandler { ModelType = "LoRA" };
            Program.T2IModelSets["LoRA"] = handler;
        }
        T2IModel lora = new(handler, "/tmp", $"/tmp/{name}.safetensors", $"{name}.safetensors");
        handler.Models[lora.Name] = lora;
    }

    private static WorkflowGenerator Generator() => new()
    {
        UserInput = new T2IParamInput(null),
        Features = [],
        ModelFolderFormat = "/",
        Workflow = [],
    };

    private static (WGNodeData Media, WGNodeData Vae) AddStageData(
        WorkflowGenerator generator,
        string firstId)
    {
        int start = int.Parse(firstId);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        UnknownNode media = bridge.AddStub(
            "UnitTest_Media",
            $"{start}").WithOutputs(WGNodeData.DT_VIDEO);
        UnknownNode vae = bridge.AddStub(
            "UnitTest_Vae",
            $"{start + 1}").WithOutputs(WGNodeData.DT_VAE);
        UnknownNode audio = bridge.AddStub(
            "UnitTest_Audio",
            $"{start + 2}").WithOutputs(WGNodeData.DT_AUDIO);
        WGNodeData mediaData = new(
            new JArray(media.Id, 0),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Width = 512,
            Height = 512,
            Frames = 25,
            FPS = 24,
            AttachedAudio = new(
                new JArray(audio.Id, 0),
                generator,
                WGNodeData.DT_AUDIO,
                null),
        };
        WGNodeData vaeData = new(
            new JArray(vae.Id, 0),
            generator,
            WGNodeData.DT_VAE,
            null);
        return (mediaData, vaeData);
    }

    private static void AssertStageRef(
        StageRefStore.StageRef stageRef,
        string mediaId,
        string vaeId,
        string audioId)
    {
        Assert.NotNull(stageRef);
        Assert.Equal(mediaId, $"{stageRef.Media.Path[0]}");
        Assert.Equal(vaeId, $"{stageRef.Vae.Path[0]}");
        Assert.NotNull(stageRef.Media.AttachedAudio);
        Assert.Equal(audioId, $"{stageRef.Media.AttachedAudio.Path[0]}");
    }
}
