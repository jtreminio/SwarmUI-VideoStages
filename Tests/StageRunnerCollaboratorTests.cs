using System.Collections.Immutable;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using VideoStages.Execution;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime;
using VideoStages.Architectures.Ltx2.Runtime.Chain;
using VideoStages.Architectures.Ltx2.Runtime.Stage;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageRunnerCollaboratorTests
{
    [Fact]
    public void Stage_runner_collaborators_accept_compiled_plans_not_authored_specs()
    {
        AssertTypedMethod(
            typeof(PlannedStagePromptResolver),
            nameof(PlannedStagePromptResolver.Resolve),
            typeof(ClipPlan),
            typeof(StagePlan));
        AssertTypedMethod(
            typeof(StageContextBuilder),
            nameof(StageContextBuilder.Build),
            typeof(StagePlan),
            typeof(int),
            typeof(ClipContext),
            typeof(bool));
        AssertTypedMethod(
            typeof(StageSourceMediaResolver),
            nameof(StageSourceMediaResolver.Resolve),
            typeof(ClipContext),
            typeof(StagePlan),
            typeof(int),
            typeof(LtxPostVideoChain));
    }

    [Fact]
    public void Planned_prompt_resolver_uses_compiled_clip_and_stage_identity()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = new(null);
        input.Set(
            T2IParamTypes.Prompt,
            "global words <videoclip[7]>clip words <videoclip[7,0]>stage words");
        input.Set(
            T2IParamTypes.NegativePrompt,
            "global negative <videoclip[7,0]>stage negative");
        WorkflowGenerator generator = new() { UserInput = input };
        (ClipPlan clip, StagePlan stage) = MakePlan();

        (string positive, string negative) =
            new PlannedStagePromptResolver(generator).Resolve(clip, stage);

        Assert.Contains("clip words", positive);
        Assert.Contains("stage words", positive);
        Assert.DoesNotContain("global words", positive);
        Assert.Equal("stage negative", negative);
    }

    [Fact]
    public void Host_stage_runner_rejects_a_missing_stage_output()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        WorkflowGenerator generator = new()
        {
            Workflow = [],
            UserInput = new(null)
        };
        VideoExecutionPlan plan = MakeExecutionPlan(models.VideoModel.Name);
        ClipPlan clip = Assert.Single(plan.Clips);
        using VideoStageRunner stageRunner = new(generator, plan);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => stageRunner.Execute(
                clip,
                (plannedClip, stage) => { },
                (plannedClip, stage, continuation, sectionId) => false));

        Assert.Contains($"stage {clip.Stages[0].StageId}", error.Message);
        Assert.Contains("produced no media artifact", error.Message);
    }

    /// <summary>
    /// The global frame trim belongs to the timeline, not to a stage. A stage-level trim would run
    /// before the timeline mixes authored audio tracks over the clip, publishing audio that is
    /// longer than, and offset from, the trimmed video.
    /// </summary>
    [Fact]
    public void Host_stage_runner_leaves_the_global_trim_to_the_timeline()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 2);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 3);
        WorkflowGenerator generator = new()
        {
            Workflow = [],
            UserInput = input
        };
        using (WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow))
        {
            UnknownNode video = bridge.AddStub("UnitTestVideo", "50")
                .WithOutputs(WGNodeData.DT_VIDEO);
            generator.CurrentMedia = video.GetOutput(0).ToWGMedia(
                generator,
                WGNodeData.DT_VIDEO,
                width: 512,
                height: 512,
                frames: 25,
                fps: 24);
        }
        VideoExecutionPlan plan = MakeExecutionPlan(models.VideoModel.Name);
        ClipPlan clip = Assert.Single(plan.Clips);
        using VideoStageRunner stageRunner = new(generator, plan);

        DecodedClipArtifact output = stageRunner.Execute(
            clip,
            (plannedClip, stage) => { },
            (plannedClip, stage, continuation, sectionId) => false);

        using WorkflowBridge outputBridge = WorkflowBridge.Create(generator.Workflow);
        Assert.DoesNotContain(
            outputBridge.Graph.Nodes.Values,
            node => node.ClassTypeName == SwarmTrimFramesNode.ClassType);
        Assert.Equal(25, output.Frames);
        Assert.Equal(25, generator.CurrentMedia.Frames);
    }

    [Fact]
    public void Stage_dimension_rules_apply_the_default_ltx_grid_without_an_ic_lora()
    {
        StagePlan stage = MakePlan().Stage;
        stage = stage with
        {
            ArchitecturePayload = stage.RequireLtx2Payload() with
            {
                IcLoras = ImmutableArray<IcLoraPlan>.Empty
            }
        };

        Assert.Equal((640, 352), StageDimensionRules.SnapForIcLora(stage, 638, 359));
    }

    [Fact]
    public void Stage_dimension_rules_own_aligned_upscale_dimensions()
    {
        StagePlan stage = MakePlan().Stage with
        {
            ArchitecturePayload = MakePlan().Stage.RequireLtx2Payload() with
            {
                IcLoras = ImmutableArray<IcLoraPlan>.Empty,
                Core = MakePlan().Stage.Core with
                {
                    Upscale = new(
                        StageUpscaleMode.Latent,
                        Factor: 2,
                        RawMethod: "latent-bilinear",
                        MethodName: "bilinear"),
                },
            }
        };

        Assert.Equal((1280, 704), StageDimensionRules.ResolveUpscaled(stage, 638, 359));
    }

    [Fact]
    public void Shared_upscale_dimensions_align_before_grid_snap()
    {
        Assert.Equal(
            (288, 448),
            StageUpscaleGraph.ResolveTargetDimensions(256, 416, 1.1));
    }

    /// <summary>A projection that disagrees with the runtime warns about a conform that will not
    /// happen.</summary>
    [Fact]
    public void Host_video_geometry_projects_the_dimensions_the_runtime_will_produce()
    {
        StagePlan template = MakePlan().Stage;
        StagePlan stage = template with
        {
            Input = StageInputKind.PreviousStage,
            ArchitecturePayload = new StockHostVideoStagePayload(
                new("unit-test"),
                "unit-test-model",
                "unit-test-compatibility",
                LoraTarget.ModelOnly,
                template.Core with
                {
                    Upscale = new(StageUpscaleMode.Pixel, 1.75, "unit-test", "unit-test"),
                }),
        };

        (int Width, int Height) projected =
            HostVideoStageGeometry.ProjectFinalDimensions([stage], 832, 480);

        Assert.Equal((1472, 832), projected);
    }

    [Theory]
    [InlineData((int)StageUpscaleMode.Pixel, 768)]
    [InlineData((int)StageUpscaleMode.Model, 768)]
    [InlineData((int)StageUpscaleMode.Latent, 512)]
    [InlineData((int)StageUpscaleMode.LatentModel, 512)]
    [InlineData((int)StageUpscaleMode.Unsupported, 512)]
    public void Host_video_geometry_projects_only_executable_decoded_upscales(
        int modeValue,
        int expected)
    {
        StageUpscaleMode mode = (StageUpscaleMode)modeValue;
        StagePlan template = MakePlan().Stage;
        StagePlan stage = template with
        {
            Input = StageInputKind.PreviousStage,
            ArchitecturePayload = new StockHostVideoStagePayload(
                new("unit-test"),
                "unit-test-model",
                "unit-test-compatibility",
                LoraTarget.ModelOnly,
                template.Core with
                {
                    Upscale = new(mode, 1.5, "unit-test", "unit-test"),
                }),
        };

        Assert.Equal(
            (expected, expected),
            HostVideoStageGeometry.ProjectFinalDimensions([stage], 512, 512));
    }

    [Fact]
    public void Dimension_snap_prefers_aspect_and_clamps_its_candidate_grid()
    {
        Assert.Equal((1280, 704), DimensionSnap.Snap(1232, 688, 64));
        Assert.Equal((32, 32), DimensionSnap.Snap(20, 10));
        Assert.Equal((4096, 2976), DimensionSnap.Snap(5000, 3000));
    }

    [Fact]
    public void Dimension_snap_ties_prefer_the_larger_area()
    {
        double midpoint = Math.Sqrt(32 * 64);
        Assert.Equal((64, 64), DimensionSnap.Snap(midpoint, midpoint));
    }

    private static void AssertTypedMethod(Type type, string name, params Type[] parameters)
    {
        System.Reflection.MethodInfo method = type.GetMethod(name);
        Assert.NotNull(method);
        Assert.Equal(parameters, method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static (ClipPlan Clip, StagePlan Stage) MakePlan()
    {
        ClipPlan plannedClip = Assert.Single(MakeExecutionPlan().Clips);
        return (plannedClip, Assert.Single(plannedClip.Stages));
    }

    private static VideoExecutionPlan MakeExecutionPlan(string modelName = "unit-test-model")
    {
        StageSpec stage = new(
            Id: 31,
            Control: 1,
            Upscale: 1,
            UpscaleMethod: "",
            Model: modelName,
            Steps: 8,
            CfgScale: 1,
            Sampler: "euler",
            Scheduler: "normal",
            ImageReference: "Generated");
        ClipSpec clip = new(
            Id: 7,
            Frames: 25,
            AudioSource: MediaSource.Native,
            IcLoras: [],
            SaveAudioTrack: false,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: false,
            UploadedAudio: null,
            FrameRefs: [],
            Stages: [stage]);
        return TestPlanCompiler.Compile(
            new TimelineSpec(512, 512, 24, false, [clip]));
    }
}
