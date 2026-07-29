using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// The Wan module's own contract: exact recognition, a coherent capability profile, and opaque clip
/// compilation. Execution is covered separately; nothing here builds a graph.
/// </summary>
[Collection("VideoStagesTests")]
public class WanArchitectureTests
{
    [Fact]
    public void Recognizes_only_the_wan_2_2_image_to_video_model_class()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle wan = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel ltxVideo = TestModelFactory.CreateBaseAndLtxv2VideoModels().VideoModel;

        Assert.True(WanArchitectureModule.Instance.TryResolveModel(
            wan.VideoModel,
            out ResolvedVideoModel resolved));
        Assert.Equal(WanArchitectureModule.ArchitectureId, resolved.ArchitectureId);
        Assert.Equal(WanArchitectureModule.ImageToVideoProfileId, resolved.ModelProfileId);
        Assert.Equal(wan.VideoModel.Name, resolved.ModelName);

        Assert.False(WanArchitectureModule.Instance.TryResolveModel(wan.BaseModel, out _));
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(ltxVideo, out _));
        Assert.False(Ltx2ArchitectureModule.Instance.TryResolveModel(wan.VideoModel, out _));

        // Sharing Wan's compat class is not enough; another class in it is another architecture.
        wan.VideoModel.ModelClass = wan.VideoModel.ModelClass with
        {
            ID = "wan-2_1-image2video-14b",
        };
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(wan.VideoModel, out _));
    }

    [Fact]
    public void Descriptor_is_a_valid_registration_that_publishes_cut_only_joins()
    {
        // Construction is the registration contract: profiles, default profile, and a rule for
        // every boundary mode are all validated here.
        IVideoArchitectureRegistry registry = new VideoArchitectureRegistry(
            [NoneArchitectureModule.Instance, WanArchitectureModule.Instance]);

        VideoArchitectureDescriptor descriptor = Assert.Single(
            registry.Catalog,
            candidate => candidate.Id == WanArchitectureModule.ArchitectureId);
        Assert.Equal(
            RuleSupport.Supported,
            descriptor.BoundaryRules[BoundaryExecutionMode.Cut].Support);
        Assert.Equal(
            RuleSupport.Unsupported,
            descriptor.BoundaryRules[BoundaryExecutionMode.Continue].Support);
        Assert.Equal(
            RuleSupport.Unsupported,
            descriptor.BoundaryRules[BoundaryExecutionMode.Crossfade].Support);
        Assert.Equal(
            WanArchitectureModule.FrameGrid,
            Assert.Single(descriptor.Profiles).FrameGrid);
    }

    [Fact]
    public void Capability_validation_rejects_what_the_first_slice_does_not_support()
    {
        StageSpec stage = Stage(10, "wan-model");

        AssertRejected(GeneratedClip(0, stage with { Upscale = 2 }), "upscale");
        AssertRejected(
            GeneratedClip(0, stage with { RetakeWindow = new(0, 8, 1) }),
            "retake");
        AssertRejected(
            GeneratedClip(0, stage with { Loras = [new LoraRef("wan-lora.safetensors")] }),
            "normal LoRA");
        AssertRejected(
            GeneratedClip(0, stage) with { ImageRefs = [new("Generated", 1, false, null)] },
            "image references");
        AssertRejected(
            GeneratedClip(0, stage) with { PromptWindows = [new("late", 1, 1)] },
            "prompt relay");
        AssertRejected(
            GeneratedClip(0, stage) with
            {
                IcLoras = [new("wan-ic.safetensors", "Upload", 1, 1, "canny", null)],
            },
            "IC-LoRA");
        AssertRejected(
            GeneratedClip(0, stage) with { SourceVideo = new("data", "clip.mp4", 0) },
            "entry mode");
        AssertRejected(
            GeneratedClip(
                0,
                stage,
                stage with { Id = 11, ClipStageIndex = 1, ClipStageRawIndex = 1 }),
            "multiple active stages");
    }

    [Fact]
    public void Compilation_attaches_one_opaque_stage_payload_per_authored_stage()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, "wan-model") with { Control = 0.8 });

        ClipPlan compiled = Assert.Single(Compile(clip).Clips);

        WanClipPayload payload = compiled.RequireWanPayload();
        Assert.Equal(WanArchitectureModule.ArchitectureId, payload.ArchitectureId);
        WanStagePayload stagePayload = Assert.Single(compiled.Stages).RequireWanPayload();
        Assert.Same(stagePayload, Assert.Single(payload.Stages).Value);
        Assert.Equal("wan-model", stagePayload.Model);
        Assert.Equal(0.8, stagePayload.Control);
        Assert.Equal(12, stagePayload.Steps);
        Assert.Equal(4.5, stagePayload.CfgScale);
        Assert.Equal("euler", stagePayload.Sampler);
        Assert.Equal("normal", stagePayload.Scheduler);
    }

    /// <summary>
    /// Settings the common capability validator does not inspect. Each one would otherwise compile
    /// into a payload that silently omits it.
    /// </summary>
    [Fact]
    public void Compilation_refuses_settings_its_payload_cannot_carry()
    {
        StageSpec stage = Stage(10, "wan-model");

        AssertRefused(
            GeneratedClip(0, stage with { Control = 0 }),
            "a stage that generates nothing");
        AssertRefused(
            GeneratedClip(0, stage with { ImageReference = "Base" }),
            "stage image reference 'Base'");
        AssertRefused(GeneratedClip(0, stage) with { ReuseAudio = true }, "audio reuse");
        AssertRefused(
            GeneratedClip(0, stage) with { ClipLengthFromAudio = true },
            "clip length from audio");
        AssertRefused(
            GeneratedClip(0, stage) with { ClipLengthFromControlNet = true },
            "clip length from ControlNet");
    }

    private static void AssertRejected(ClipSpec clip, string expectedOption) =>
        AssertBlocked(clip, "architecture-capability-unsupported", expectedOption);

    private static void AssertRefused(ClipSpec clip, string expectedOption) =>
        AssertBlocked(clip, "wan22.option.unsupported", expectedOption);

    private static void AssertBlocked(ClipSpec clip, string code, string expectedOption)
    {
        VideoExecutionPlan plan = Compile(clip);

        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == code
                && item.Severity == PlanDiagnosticSeverity.Error
                && item.Message.Contains(expectedOption));
        Assert.Null(Assert.Single(plan.Clips).ArchitecturePayload);
    }

    private static VideoExecutionPlan Compile(ClipSpec clip)
    {
        VideoStagesSpec spec = new(512, 512, 24, false, [clip]);
        return VideoExecutionPlanCompiler.Compile(
            spec,
            RootEnvironment.FromSpec(spec),
            ResolveWan(spec));
    }

    /// <summary>Assigns Wan to every clip without needing host model metadata installed.</summary>
    private static ArchitecturePlanningResult ResolveWan(VideoStagesSpec spec)
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        Dictionary<int, ClipArchitectureAssignment> clips = [];
        foreach (ClipSpec clip in spec.Clips)
        {
            Dictionary<int, ResolvedVideoModel> stageModels = [];
            foreach (StageSpec stage in clip.Stages ?? [])
            {
                stageModels[stage.ClipStageRawIndex] = new(
                    stage.Model,
                    descriptor.Id,
                    WanArchitectureModule.ImageToVideoProfileId,
                    descriptor);
            }
            clips.TryAdd(clip.Id, new(
                clip.Id,
                WanArchitectureModule.Instance,
                descriptor,
                stageModels));
        }
        return new(clips, []);
    }

    private static ClipSpec GeneratedClip(int id, params StageSpec[] stages) =>
        new(
            id,
            25,
            Constants.AudioSourceNative,
            [],
            false,
            false,
            false,
            false,
            null,
            [],
            stages);

    private static StageSpec Stage(int id, string model) =>
        new(id, 1, 1, "pixel-lanczos", model, 12, 4.5, "euler", "normal", "Generated");
}
