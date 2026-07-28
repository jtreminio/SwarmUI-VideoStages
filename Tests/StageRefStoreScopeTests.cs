using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class StageRefStoreScopeTests
{
    private static readonly ArchitectureId Ltx = new("ltx2");
    private static readonly ArchitectureId Wan = new("wan");

    [Fact]
    public void Default_architecture_id_is_rejected_before_writing_keys()
    {
        WorkflowGenerator generator = Generator();

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new StageRefStore(generator, default));

        Assert.Equal("architectureId", error.ParamName);
        Assert.Empty(generator.NodeHelpers);
    }

    [Fact]
    public void Architecture_scopes_capture_same_kind_independently()
    {
        WorkflowGenerator generator = Generator();
        StageRefStore ltx = new(generator, Ltx);
        StageRefStore wan = new(generator, Wan);
        (WGNodeData Media, WGNodeData Vae) ltxData =
            AddStageData(generator, "100");
        (WGNodeData Media, WGNodeData Vae) wanData =
            AddStageData(generator, "200");

        ltx.Capture(StageRefStore.StageKind.Base, ltxData.Media, ltxData.Vae);
        wan.Capture(StageRefStore.StageKind.Base, wanData.Media, wanData.Vae);

        AssertStageRef(ltx.Base, "100", "101", "102");
        AssertStageRef(wan.Base, "200", "201", "202");
        Assert.Equal(
            [
                "videostages.arch.ltx2.stage-ref.base.audio",
                "videostages.arch.ltx2.stage-ref.base.media",
                "videostages.arch.ltx2.stage-ref.base.vae",
                "videostages.arch.wan.stage-ref.base.audio",
                "videostages.arch.wan.stage-ref.base.media",
                "videostages.arch.wan.stage-ref.base.vae",
            ],
            generator.NodeHelpers.Keys.Order().ToArray());
        Assert.DoesNotContain("videostages.base.media", generator.NodeHelpers.Keys);
        Assert.DoesNotContain("videostages.base.vae", generator.NodeHelpers.Keys);
        Assert.DoesNotContain("videostages.base.media.audio", generator.NodeHelpers.Keys);
    }

    [Fact]
    public void Same_architecture_scope_interoperates_across_store_instances()
    {
        WorkflowGenerator generator = Generator();
        StageRefStore capture = new(generator, Ltx);
        StageRefStore read = new(generator, Ltx);
        (WGNodeData Media, WGNodeData Vae) data =
            AddStageData(generator, "300");

        capture.Capture(
            StageRefStore.StageKind.Generated,
            data.Media,
            data.Vae);

        AssertStageRef(read.Generated, "300", "301", "302");
    }

    [Fact]
    public void Pre_root_handoff_cleans_only_its_architecture_scope()
    {
        WorkflowGenerator generator = Generator();
        StageRefStore ltx = new(generator, Ltx);
        StageRefStore wan = new(generator, Wan);
        (WGNodeData Media, WGNodeData Vae) ltxData =
            AddStageData(generator, "400");
        (WGNodeData Media, WGNodeData Vae) wanData =
            AddStageData(generator, "500");
        ltx.Capture(
            StageRefStore.StageKind.PreRootVideo,
            ltxData.Media,
            ltxData.Vae);
        wan.Capture(
            StageRefStore.StageKind.PreRootVideo,
            wanData.Media,
            wanData.Vae);
        string allNodeIds;
        using (WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow))
        {
            allNodeIds = string.Join(",", bridge.Graph.Nodes.Keys);
        }
        generator.NodeHelpers[ltx.PreCoreNodeIdsKey] = allNodeIds;
        generator.NodeHelpers[wan.PreCoreNodeIdsKey] = "wan-owned-snapshot";

        new RootVideoStageHandoff(generator, ltx)
            .DropCoreImageToVideoOutput();

        Assert.Null(ltx.PreRootVideo);
        Assert.False(generator.NodeHelpers.ContainsKey(ltx.PreCoreNodeIdsKey));
        AssertStageRef(wan.PreRootVideo, "500", "501", "502");
        Assert.Equal(
            "wan-owned-snapshot",
            generator.NodeHelpers[wan.PreCoreNodeIdsKey]);
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
