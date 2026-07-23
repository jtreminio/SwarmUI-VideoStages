using System.Collections.Immutable;
using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.Architectures.Ltx2;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

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
            typeof(StageFramePreparer),
            nameof(StageFramePreparer.Prepare),
            typeof(StagePlan),
            typeof(int),
            typeof(ClipContext),
            typeof(StageExecutionOptions),
            typeof(RootExecutionPolicy));
        AssertTypedMethod(
            typeof(StageUpscaleGraphBuilder),
            nameof(StageUpscaleGraphBuilder.Apply),
            typeof(ClipContext),
            typeof(StagePlan),
            typeof(int),
            typeof(LtxPostVideoChainCapture));
        AssertTypedMethod(
            typeof(IcLoraStageInputResolver),
            nameof(IcLoraStageInputResolver.Resolve),
            typeof(StageFrame));
        AssertTypedMethod(
            typeof(StageRuntimeArtifactCapture),
            nameof(StageRuntimeArtifactCapture.Capture),
            typeof(StagePlan));

        Type[] collaboratorTypes =
        [
            typeof(PlannedStagePromptResolver),
            typeof(StageFramePreparer),
            typeof(StageUpscaleGraphBuilder),
            typeof(IcLoraStageInputResolver),
            typeof(StageRuntimeArtifactCapture),
        ];
        Assert.DoesNotContain(
            collaboratorTypes.SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly)),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(ClipSpec)
                || parameter.ParameterType == typeof(StageSpec)));
        Assert.DoesNotContain(
            typeof(StageRunner).GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(ClipSpec)));
    }

    [Fact]
    public void Clip_runtime_context_and_execution_context_are_plan_only()
    {
        Assert.Equal(
            [
                typeof(VideoExecutionPlan),
                typeof(ClipPlan),
                typeof(WGNodeData),
                typeof(WGNodeData),
            ],
            Assert.Single(typeof(ClipContext).GetConstructors()).GetParameters()
                .Select(parameter => parameter.ParameterType));
        Assert.Null(typeof(ClipContext).GetProperty("Clip"));
        Assert.DoesNotContain(
            typeof(StageClipExecutionContext).GetProperties(),
            property => property.PropertyType == typeof(ClipSpec));
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
    public void Runtime_artifact_capture_rejects_a_missing_stage_output()
    {
        WorkflowGenerator generator = new()
        {
            Workflow = new JObject(),
            UserInput = new(null)
        };
        StagePlan stage = MakePlan().Stage;

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => new StageRuntimeArtifactCapture(generator).Capture(stage));

        Assert.Contains($"stage {stage.StageId}", error.Message);
    }

    [Fact]
    public void Runtime_artifact_capture_preserves_stage_output_media()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            Workflow = workflow,
            UserInput = new(null)
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        _ = bridge.AddStub("UnitTestVideo", "901").WithOutputs(WGNodeData.DT_VIDEO);
        generator.CurrentMedia = new WGNodeData(
            new JArray("901", 0),
            generator,
            WGNodeData.DT_VIDEO,
            T2IModelClassSorter.CompatLtxv2);

        RuntimeArtifact artifact = new StageRuntimeArtifactCapture(generator)
            .Capture(MakePlan().Stage);

        Assert.True(artifact.HasMedia);
        Assert.Equal(ArtifactOrigin.StageOutput, artifact.Origin);
        generator.CurrentMedia = null;
        artifact.PublishTo(generator);
        Assert.Equal("901", $"{generator.CurrentMedia.Path[0]}");
    }

    [Fact]
    public void Stage_dimension_rules_leave_unconstrained_stage_dimensions_unchanged()
    {
        StagePlan stage = MakePlan().Stage;
        stage = stage with
        {
            ArchitecturePayload = stage.RequireLtx2Payload() with
            {
                IcLoras = ImmutableArray<IcLoraPlan>.Empty
            }
        };

        Assert.Equal((638, 359), StageDimensionRules.SnapForIcLora(stage, 638, 359));
    }

    [Fact]
    public void Stage_dimension_rules_own_aligned_upscale_dimensions()
    {
        StagePlan stage = MakePlan().Stage with
        {
            ArchitecturePayload = MakePlan().Stage.RequireLtx2Payload() with
            {
                IcLoras = ImmutableArray<IcLoraPlan>.Empty,
                Upscale = new(
                    StageUpscaleMode.Latent,
                    Factor: 2,
                    RawMethod: "latent-bilinear",
                    MethodName: "bilinear"),
            }
        };

        Assert.Equal((1264, 704), StageDimensionRules.ResolveUpscaled(stage, 638, 359));
    }

    private static void AssertTypedMethod(Type type, string name, params Type[] parameters)
    {
        System.Reflection.MethodInfo method = type.GetMethod(name);
        Assert.NotNull(method);
        Assert.Equal(parameters, method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static (ClipPlan Clip, StagePlan Stage) MakePlan()
    {
        StageSpec stage = new(
            Id: 31,
            Control: 1,
            Upscale: 1,
            UpscaleMethod: "",
            Model: "unit-test-model",
            Steps: 8,
            CfgScale: 1,
            Sampler: "euler",
            Scheduler: "normal",
            ImageReference: "Generated");
        ClipSpec clip = new(
            Id: 7,
            Frames: 25,
            AudioSource: Constants.AudioSourceNative,
            IcLoras: [],
            SaveAudioTrack: false,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: false,
            UploadedAudio: null,
            ImageRefs: [],
            Stages: [stage]);
        ClipPlan plannedClip = Assert.Single(
            TestPlanCompiler.Compile(
                new VideoStagesSpec(512, 512, 24, false, [clip]))
            .Clips);
        return (plannedClip, Assert.Single(plannedClip.Stages));
    }
}
