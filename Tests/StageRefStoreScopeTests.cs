using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class StageRefStoreScopeTests
{
    [Fact]
    public void Every_owned_runtime_key_family_uses_the_ltx_architecture_prefix()
    {
        List<string> runtimeKeys =
        [
            LtxRuntimeKeyScope.ControlNetNormalized,
            LtxRuntimeKeyScope.ControlNetFullImage(2),
            LtxRuntimeKeyScope.ControlNetFrameCount(2),
            LtxRuntimeKeyScope.IcLoraAudioReference(3, 4),
            LtxRuntimeKeyScope.IcLoraAudioReference(3, 4, 5),
            LtxRuntimeKeyScope.IcLoraControlSignal(3, 4),
            LtxRuntimeKeyScope.IcLoraUploadedDriveImages(3, 4),
        ];
        runtimeKeys.AddRange(
            from StageRefStore.StageKind kind in Enum.GetValues<StageRefStore.StageKind>()
            from key in new[]
            {
                LtxRuntimeKeyScope.StageRefMedia(kind),
                LtxRuntimeKeyScope.StageRefVae(kind),
                LtxRuntimeKeyScope.StageRefAudio(kind),
            }
            select key);

        Assert.All(
            runtimeKeys,
            key => Assert.StartsWith("videostages.arch.ltx2.", key));
        Assert.Equal(runtimeKeys.Count, runtimeKeys.Distinct().Count());
    }

    /// <summary>
    /// Every runtime handoff the LTX-2 path writes into <c>NodeHelpers</c> is namespaced to the
    /// architecture, so a second architecture in the same request cannot collide with it. Generated
    /// through the real POST path over the widest key-writing shape available: two stages, a pixel
    /// upscale, and an IC-LoRA with an uploaded drive video and a control signal.
    /// </summary>
    [Fact]
    public async Task A_generated_ltx_workflow_writes_no_unscoped_runtime_key()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");
        JObject clip = Fixtures.MakeClip(
            fixture.Stage(steps: 10),
            fixture.Stage(upscale: 2, steps: 10));
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "UnitTest_IcLoraA",
            ["driveSource"] = MediaSource.Upload,
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
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(Fixtures.MakeDocument(clip)));

        // Exclude the keys no architecture owns: ControlNet capture, and the execution host's
        // record of what the root cleanup swept.
        string[] neutralPrefixes = ["videostages.controlnet.", "videostages.host-root."];
        string[] videoStagesKeys = [.. generator.NodeHelpers.Keys
            .Where(key => key.StartsWith("videostages.", StringComparison.Ordinal))
            .Where(key => !neutralPrefixes.Any(
                prefix => key.StartsWith(prefix, StringComparison.Ordinal)))];

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
