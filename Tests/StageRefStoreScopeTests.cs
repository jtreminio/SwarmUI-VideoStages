using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class StageRefStoreScopeTests
{
    [Fact]
    public void Every_owned_runtime_key_family_uses_the_ltx_architecture_prefix()
    {
        LtxRuntimeKeyScope keys = new();
        List<string> runtimeKeys =
        [
            keys.PreCoreNodeIds,
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

    [Fact]
    public void Pre_root_handoff_cleans_ltx_scope_without_touching_unrelated_helpers()
    {
        WorkflowGenerator generator = Generator();
        StageRefStore ltx = new(generator);
        (WGNodeData Media, WGNodeData Vae) ltxData =
            AddStageData(generator, "400");
        ltx.Capture(
            StageRefStore.StageKind.PreRootVideo,
            ltxData.Media,
            ltxData.Vae);
        string allNodeIds;
        using (WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow))
        {
            allNodeIds = string.Join(",", bridge.Graph.Nodes.Keys);
        }
        generator.NodeHelpers[ltx.PreCoreNodeIdsKey] = allNodeIds;
        const string unrelatedKey = "host.runtime.marker";
        generator.NodeHelpers[unrelatedKey] = "unrelated-runtime-value";

        new RootVideoStageHandoff(generator, ltx)
            .DropCoreImageToVideoOutput();

        Assert.Null(ltx.PreRootVideo);
        Assert.False(generator.NodeHelpers.ContainsKey(ltx.PreCoreNodeIdsKey));
        Assert.Equal(
            "unrelated-runtime-value",
            generator.NodeHelpers[unrelatedKey]);
        Assert.DoesNotContain(
            "videostages.pre-core-node-ids",
            generator.NodeHelpers.Keys);
        Assert.DoesNotContain(
            "videostages.preroot.media",
            generator.NodeHelpers.Keys);
        Assert.True(JToken.DeepEquals(
            generator.CurrentMedia.Path,
            new JArray("400", 0)));
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
