using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Wan;
using VideoStages.Authoring;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class HostVideoArchitectureTests
{
    [Theory]
    [InlineData("hunyuan-video-1_5", "hunyuan-video-1_5")]
    [InlineData("hunyuan-video", "hunyuan-video")]
    [InlineData("hunyuan-video-skyreels", "hunyuan-video")]
    [InlineData("hunyuan-video-skyreels-i2v", "hunyuan-video")]
    [InlineData("hunyuan-video-i2v", "hunyuan-video")]
    [InlineData("hunyuan-video-i2v-v2", "hunyuan-video")]
    [InlineData("genmo-mochi-1", "genmo-mochi-1")]
    [InlineData("kandinsky5-video-lite", "kandinsky5-vidlite")]
    [InlineData("kandinsky5-video-pro", "kandinsky5-vidpro")]
    [InlineData("nvidia-cosmos-1-7b-text2world", "nvidia-cosmos-1")]
    [InlineData("nvidia-cosmos-1-14b-video2world", "nvidia-cosmos-1")]
    [InlineData("lightricks-ltx-video", "lightricks-ltx-video")]
    [InlineData("lightricks-ltx-video-2", "lightricks-ltx-video-2")]
    public void Resolves_video_compatibilities_through_the_host_fallback(
        string modelClassId,
        string compatibilityClassId)
    {
        using SwarmUiTestContext context = new();
        T2IModel model = Model(modelClassId, Compatibility(compatibilityClassId));

        Assert.True(HostVideoArchitectureModule.Instance.TryResolveModel(
            model,
            out ResolvedVideoModel resolved));
        Assert.Equal(
            HostVideoArchitectureModule.ArchitectureId,
            resolved.ArchitectureId);
        Assert.True(HostVideoArchitectureModule.Instance.IsFallback);
        Assert.Equal(
            model.ModelClass.CompatClass.LorasTargetTextEnc,
            resolved.LorasTargetTextEncoder);
    }

    [Fact]
    public void Resolved_LoRA_target_fact_follows_core_instead_of_a_compatibility_allowlist()
    {
        using SwarmUiTestContext context = new();
        T2IModelCompatClass compatibility = Compatibility("nvidia-cosmos-1")
            with { LorasTargetTextEnc = false };

        Assert.True(HostVideoArchitectureModule.Instance.TryResolveModel(
            Model("nvidia-cosmos-1-7b-text2world", compatibility),
            out ResolvedVideoModel resolved));

        Assert.False(resolved.LorasTargetTextEncoder);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Compilation_uses_the_resolved_core_LoRA_target_fact(
        bool targetsTextEncoder,
        int expectedLoras)
    {
        using SwarmUiTestContext context = new();
        T2IModelCompatClass compatibility = Compatibility("nvidia-cosmos-1")
            with { LorasTargetTextEnc = targetsTextEncoder };
        T2IModel model = Model(
            "nvidia-cosmos-1-7b-text2world",
            compatibility);
        Assert.True(HostVideoArchitectureModule.Instance.TryResolveModel(
            model,
            out ResolvedVideoModel resolved));
        StageSpec stage = new(
            10,
            1,
            1,
            "pixel-lanczos",
            model.Name,
            12,
            4.5,
            "euler",
            "normal",
            "Generated",
            Loras: [new("text-only.safetensors", 0, 0.8)]);
        ClipSpec clip = new(
            0,
            25,
            Constants.AudioSourceNative,
            [],
            false,
            false,
            false,
            false,
            null,
            [],
            [stage]);

        ArchitectureClipCompilation compilation =
            HostVideoArchitectureModule.Instance.ValidateAndCompileClip(
                clip,
                new Dictionary<int, ResolvedVideoModel> { [0] = resolved },
                new(512, 512, 24, ArchitectureEntryMode.ImageToVideo));

        StockHostVideoStagePayload payload = Assert.IsType<StockHostVideoStagePayload>(
            compilation.StagePayloads[0]);
        Assert.Equal(
            targetsTextEncoder
                ? NormalLoraTargetPolicy.ModelAndTextEncoder
                : NormalLoraTargetPolicy.ModelOnly,
            payload.LoraTargetPolicy);
        Assert.Equal(expectedLoras, payload.Core.Loras.Length);
    }

    [Theory]
    [InlineData("invented-video", "invented-video", true)]
    [InlineData("stable-video-diffusion-img2vid-v1", "stable-video-diffusion-img2vid-v1", true)]
    [InlineData("hunyuan-video-1_5-sr", "hunyuan-video-1_5", true)]
    [InlineData("lightricks-ltx-video/vae", "lightricks-ltx-video", true)]
    [InlineData("hunyuan-video-1_5/lora", "hunyuan-video-1_5", false)]
    [InlineData("lightricks-ltx-video/control-lora", "lightricks-ltx-video", false)]
    public void Unlisted_video_classes_resolve_unless_the_class_is_a_LoRA(
        string modelClassId,
        string compatibilityClassId,
        bool expectResolved)
    {
        using SwarmUiTestContext context = new();

        bool didResolve = HostVideoArchitectureModule.Instance.TryResolveModel(
            Model(modelClassId, Compatibility(compatibilityClassId)),
            out ResolvedVideoModel resolved);

        Assert.Equal(expectResolved, didResolve);
        if (!expectResolved)
        {
            Assert.Null(resolved);
            return;
        }
        Assert.Equal(modelClassId, resolved.ModelClassId);
        Assert.Equal(compatibilityClassId, resolved.CompatibilityClassId);
    }

    [Theory]
    [InlineData("nvidia-cosmos-predict2-t2i-2b", "nvidia-cosmos-predict2-t2i-2b")]
    [InlineData("nvidia-cosmos-predict2-t2i-14b", "nvidia-cosmos-predict2-t2i-14b")]
    public void Rejects_Cosmos_Predict2_text_to_image_false_positives(
        string modelClassId,
        string compatibilityClassId)
    {
        using SwarmUiTestContext context = new();

        Assert.False(HostVideoArchitectureModule.Instance.TryResolveModel(
            Model(modelClassId, Compatibility(compatibilityClassId)),
            out _));
    }

    [Fact]
    public void Production_specialized_modules_still_own_LTX_23_and_WAN()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle ltx = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        TestModelBundle wan =
            TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            ltx.VideoModel,
            out ResolvedVideoModel resolvedLtx));
        Assert.Equal(Ltx2ArchitectureModule.ArchitectureId, resolvedLtx.ArchitectureId);
        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            wan.VideoModel,
            out ResolvedVideoModel resolvedWan));
        Assert.Equal(WanArchitectureModule.ArchitectureId, resolvedWan.ArchitectureId);
        Assert.False(resolvedWan.LorasTargetTextEncoder);
    }

    [Theory]
    [InlineData("wan-2_2-image2video-14b", "wan-21-14b")]
    [InlineData("wan-2_1-image2video-14b", "wan-21-14b")]
    [InlineData("wan-2_1-image2video-1_3b", "wan-21-1_3b")]
    [InlineData("wan-2_2-ti2v-5b", "wan-22-5b")]
    public void Production_Wan_module_owns_named_core_video_branches(
        string modelClassId,
        string compatibilityClassId)
    {
        using SwarmUiTestContext context = new();

        Assert.True(VideoArchitectureRegistry.Production.TryResolveModel(
            Model(modelClassId, Compatibility(compatibilityClassId)),
            out ResolvedVideoModel resolved));
        Assert.Equal(WanArchitectureModule.ArchitectureId, resolved.ArchitectureId);
    }

    [Fact]
    public void Specialized_resolution_wins_over_a_matching_fallback()
    {
        using SwarmUiTestContext context = new();
        T2IModel model = Model(
            "test-video",
            new()
            {
                ID = "test-video",
                IsText2Video = true,
                IsImage2Video = true,
            });
        MatchingModule specialized = new("specialized", isFallback: false);
        MatchingModule fallback = new("fallback", isFallback: true);
        VideoArchitectureRegistry registry = new([fallback, specialized]);

        Assert.True(registry.TryResolveModel(model, out ResolvedVideoModel resolved));
        Assert.Equal(new ArchitectureId("specialized"), resolved.ArchitectureId);
    }

    private static T2IModel Model(
        string modelClassId,
        T2IModelCompatClass compatibility)
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
            Name = modelClassId,
            CompatClass = compatibility,
            // A record copy bypasses T2IModelClassSorter.Register, the only place core derives
            // IsLora from the ID; without this a "/lora" class would look like a checkpoint.
            IsLora = modelClassId.Contains("/lora")
                || modelClassId.Contains("/control-lora"),
        };
        return models.VideoModel;
    }

    private static T2IModelCompatClass Compatibility(string id) => id switch
    {
        "hunyuan-video-1_5" => T2IModelClassSorter.CompatHunyuanVideo1_5,
        "hunyuan-video" => T2IModelClassSorter.CompatHunyuanVideo,
        "genmo-mochi-1" => T2IModelClassSorter.CompatGenmoMochi,
        "kandinsky5-vidlite" => T2IModelClassSorter.CompatKandinsky5VidLite,
        "kandinsky5-vidpro" => T2IModelClassSorter.CompatKandinsky5VidPro,
        "nvidia-cosmos-1" => T2IModelClassSorter.CompatCosmos,
        "nvidia-cosmos-predict2-t2i-2b" =>
            T2IModelClassSorter.CompatCosmosPredict2_2b,
        "nvidia-cosmos-predict2-t2i-14b" =>
            T2IModelClassSorter.CompatCosmosPredict2_14b,
        "lightricks-ltx-video" => T2IModelClassSorter.CompatLtxv,
        "lightricks-ltx-video-2" => T2IModelClassSorter.CompatLtxv2,
        "stable-video-diffusion-img2vid-v1" => T2IModelClassSorter.CompatSvd,
        "wan-21-14b" => T2IModelClassSorter.CompatWan21_14b,
        "wan-21-1_3b" => T2IModelClassSorter.CompatWan21_1_3b,
        "wan-22-5b" => T2IModelClassSorter.CompatWan22_5b,
        _ => new()
        {
            ID = id,
            IsText2Video = true,
            IsImage2Video = true,
        },
    };

    private sealed class MatchingModule : IVideoArchitectureModule
    {
        internal MatchingModule(string id, bool isFallback)
        {
            IsFallback = isFallback;
            Descriptor = DescriptorFor(id);
        }

        public VideoArchitectureDescriptor Descriptor { get; }

        public bool IsFallback { get; }

        public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
        {
            resolved = TestResolvedVideoModel.Create(
                model.Name,
                new("video"),
                Descriptor,
                model.ModelClass.ID,
                model.ModelClass.CompatClass.ID);
            return true;
        }

        public ArchitectureClipCompilation ValidateAndCompileClip(
            ClipSpec clip,
            IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
            ArchitectureClipCompileContext context) =>
            throw new NotSupportedException();
    }

    private static VideoArchitectureDescriptor DescriptorFor(string id)
    {
        ArchitectureId architectureId = new(id);
        RuleDecision boundary = RuleDecision.Supported(
            $"{id}.cut",
            "cut");
        return new(
            architectureId,
            id,
            [AudioSourceKind.Disabled],
            [ArchitectureEntryMode.TextToVideo],
            ArchitectureFeature.None,
            new ArchitectureBoundaryPolicy(new Dictionary<BoundaryJoinType, RuleDecision>
            {
                [BoundaryJoinType.Cut] = boundary,
                [BoundaryJoinType.Continue] = boundary,
                [BoundaryJoinType.Crossfade] = boundary,
            }));
    }
}
