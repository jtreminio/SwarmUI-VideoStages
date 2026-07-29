using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// The Wan module's own capability and payload contract. Shared recognition and descriptor
/// invariants live in <see cref="RealArchitectureContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public class WanArchitectureTests
{
    [Fact]
    public void Does_not_claim_base_or_cross_family_models()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle wan = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel ltxVideo = TestModelFactory.CreateBaseAndLtxv2VideoModels().VideoModel;

        Assert.False(WanArchitectureModule.Instance.TryResolveModel(wan.BaseModel, out _));
        Assert.False(WanArchitectureModule.Instance.TryResolveModel(ltxVideo, out _));
        Assert.False(Ltx2ArchitectureModule.Instance.TryResolveModel(wan.VideoModel, out _));
    }

    [Fact]
    public void Boundary_policy_is_cut_only_on_the_Wan_frame_grid()
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;

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
    public void Swap_compatibility_requires_the_same_architecture_and_profile()
    {
        VideoArchitectureDescriptor descriptor = WanArchitectureModule.Instance.Descriptor;
        ResolvedVideoModel swap = new(
            "wan-low",
            descriptor.Id,
            WanArchitectureModule.ImageToVideoProfileId,
            descriptor);
        ResolvedVideoModel sameProfile = swap with { ModelName = "wan-high" };
        ResolvedVideoModel otherProfile = sameProfile with
        {
            ModelProfileId = new("synthetic-other-wan-profile"),
        };
        ResolvedVideoModel otherArchitecture = sameProfile with
        {
            ArchitectureId = new("synthetic-other-architecture"),
        };

        Assert.True(WanExecutionAdapter.IsSwapCompatible(swap, sameProfile));
        Assert.False(WanExecutionAdapter.IsSwapCompatible(swap, otherProfile));
        Assert.False(WanExecutionAdapter.IsSwapCompatible(swap, otherArchitecture));
        Assert.False(WanExecutionAdapter.IsSwapCompatible(swap, null));
        string mismatch =
            WanExecutionAdapter.DescribeSwapIncompatibility(swap, 2, 10, otherProfile);
        Assert.Contains("'wan-low'", mismatch);
        Assert.Contains("clip 2 stage 10", mismatch);
        Assert.Contains("'wan-high'", mismatch);
        Assert.Contains("'synthetic-other-wan-profile'", mismatch);
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
            GeneratedClip(0, stage) with { ReuseAudio = true },
            "captured stage audio reuse");
        AssertRejected(
            GeneratedClip(0, stage) with { ClipLengthFromAudio = true },
            "audio-derived clip duration");
        AssertRejected(
            GeneratedClip(0, stage) with { ClipLengthFromControlNet = true },
            "control-signal-derived clip duration");
        AssertRejected(
            GeneratedClip(0, stage with { ImageReference = "Base" }),
            "stage image reference 'Base'");
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
        ClipSpec clip = GeneratedClip(0, Stage(10, "wan-model") with { Control = 1 });

        ClipPlan compiled = Assert.Single(Compile(clip).Clips);

        WanClipPayload payload = compiled.RequireWanPayload();
        Assert.Equal(WanArchitectureModule.ArchitectureId, payload.ArchitectureId);
        WanStagePayload stagePayload = Assert.Single(compiled.Stages).RequireWanPayload();
        Assert.Equal("wan-model", stagePayload.Model);
        Assert.Equal(1, stagePayload.Control);
        Assert.Equal(12, stagePayload.Steps);
        Assert.Equal(4.5, stagePayload.CfgScale);
        Assert.Equal("euler", stagePayload.Sampler);
        Assert.Equal("normal", stagePayload.Scheduler);
    }

    [Fact]
    public void Static_generated_frames_use_the_resolved_profile_grid_not_the_Wan_constant()
    {
        Assert.Equal(4, WanArchitectureModule.FrameGrid);
        ModelProfileId profileId = new("synthetic-grid-8");
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor with
            {
                Profiles =
                [
                    Assert.Single(WanArchitectureModule.Instance.Descriptor.Profiles) with
                    {
                        Id = profileId,
                        FrameGrid = 8,
                    },
                ],
            };
        ResolvedVideoModel resolved = new(
            "synthetic-wan",
            descriptor.Id,
            profileId,
            descriptor);

        WanStaticGeneratedFrameResolution resolution =
            WanStaticGeneratedFrameResolver.Resolve(16, 2, 10, resolved);

        Assert.Equal(8, resolution.FrameGrid);
        Assert.Equal(9, resolution.Frames);
    }

    [Fact]
    public void Static_generated_frame_resolution_fails_closed_without_a_resolved_model()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WanStaticGeneratedFrameResolver.Resolve(16, 2, 10, null));

        Assert.Equal(
            "Clip 2 stage 10 has no resolved video model.",
            error.Message);
    }

    [Fact]
    public void Static_generated_frame_resolution_fails_closed_for_an_undeclared_profile()
    {
        VideoArchitectureDescriptor descriptor =
            WanArchitectureModule.Instance.Descriptor;
        ModelProfileId undeclared = new("undeclared-grid");
        ResolvedVideoModel resolved = new(
            "synthetic-wan",
            descriptor.Id,
            undeclared,
            descriptor);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WanStaticGeneratedFrameResolver.Resolve(16, 2, 10, resolved));

        Assert.Equal(
            "Clip 2 stage 10 resolved undeclared model profile 'undeclared-grid'.",
            error.Message);
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
            GeneratedClip(0, stage with { Control = 0.8 }),
            "partial regeneration");
    }

    private static void AssertRejected(ClipSpec clip, string expectedOption)
    {
        VideoExecutionPlan plan = Compile(clip);
        AssertBlocked(
            plan,
            "architecture-capability-unsupported",
            expectedOption);
        Assert.DoesNotContain(
            plan.Diagnostics,
            item => item.Code == "wan22.option.unsupported"
                && item.Message.Contains(expectedOption));
    }

    private static void AssertRefused(ClipSpec clip, string expectedOption) =>
        AssertBlocked(Compile(clip), "wan22.option.unsupported", expectedOption);

    private static void AssertBlocked(
        VideoExecutionPlan plan,
        string code,
        string expectedOption)
    {
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
