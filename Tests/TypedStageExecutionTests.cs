using VideoStages.Planning;
using VideoStages.Execution;
using VideoStages.LTX2;
using Xunit;

namespace VideoStages.Tests;

public class TypedStageExecutionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Stage_execution_options_require_dedicated_output_for_parallel_or_intermediate(
        bool parallel,
        bool intermediates,
        bool expectedDedicated)
    {
        StageExecutionOptions options = new(parallel, intermediates);

        Assert.Equal(expectedDedicated, options.RequiresDedicatedOutput);
    }

    [Fact]
    public void Ltx_stage_execution_has_no_legacy_stage_spec_projection()
    {
        Assert.Null(typeof(StagePlan).GetMethod("ToLegacyStageSpec"));
        Assert.DoesNotContain(
            typeof(StageRunner).GetMethods(),
            method => method.Name == nameof(StageRunner.RunStage)
                && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(StageSpec));
        Assert.DoesNotContain(
            typeof(StageRunner).GetMethods(),
            method => method.Name == "RunLegacyStage");
    }

    [Fact]
    public void Stage_execution_returns_a_validatable_runtime_artifact_contract()
    {
        Assert.Equal(
            typeof(RuntimeArtifact),
            typeof(StageRunner).GetMethod(nameof(StageRunner.RunStage))?.ReturnType);
    }

    [Fact]
    public void Ltx_reference_resolution_and_conditioning_keep_the_typed_reference_plan()
    {
        Type resolver = typeof(LtxClipRefResolver);
        Type resolvedReference = typeof(ResolvedClipRef);

        Assert.Contains(
            resolver.GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic),
            method => method.Name == "ResolveStageClipRefs"
                && method.GetParameters()[0].ParameterType == typeof(ClipPlan));
        Assert.Equal(
            typeof(ImageReferencePlan),
            resolvedReference.GetProperty(nameof(ResolvedClipRef.Reference))?.PropertyType);
        Assert.Null(resolvedReference.GetProperty("Spec"));
    }

    [Fact]
    public void Ltx_prompt_execution_has_no_legacy_prompt_window_tiler()
    {
        Assert.Null(
            typeof(LtxStageExecutor).Assembly.GetType(
                "VideoStages.LTX2.PromptWindowTiler",
                throwOnError: false));
    }

    [Fact]
    public void Ltx_stage_length_resolution_accepts_the_typed_audio_length_plan()
    {
        System.Reflection.MethodInfo resolver = typeof(LtxStageLatentAudioFactory).GetMethod(
            "TryResolveControlNetLengthFrames",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(resolver);
        Assert.Equal(typeof(AudioLengthPlan), Assert.Single(resolver.GetParameters()).ParameterType);
    }

    [Fact]
    public void Ltx_stage_executor_is_only_the_stage_lifecycle_coordinator()
    {
        string[] declaredMethods = typeof(LtxStageExecutor)
            .GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal([nameof(LtxStageExecutor.RunStage)], declaredMethods);
        Assert.NotNull(typeof(LtxModelPromptPreparer).GetMethod(
            "Prepare",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(typeof(LtxStageLatentBuilder).GetMethod(
            "Build",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(typeof(LtxStageSampler).GetMethod(
            "Execute",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(typeof(LtxStageOutputFinalizer).GetMethod(
            "Complete",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void Ltx_latent_collaborators_keep_typed_plan_boundaries()
    {
        System.Reflection.MethodInfo retake = typeof(LtxRetakeMaskApplicator).GetMethod(
            "ApplyIfActive",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        System.Reflection.MethodInfo audioLength = typeof(LtxStageLatentAudioFactory).GetMethod(
            "TryResolveControlNetLengthFrames",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(retake);
        Assert.Equal(typeof(RetakePlan), retake.GetParameters()[2].ParameterType);
        Assert.NotNull(audioLength);
        Assert.Equal(typeof(AudioLengthPlan), Assert.Single(audioLength.GetParameters()).ParameterType);
        Assert.DoesNotContain(
            new[]
            {
                typeof(LtxStageLatentBuilder),
                typeof(LtxStageLatentAudioFactory),
                typeof(LtxReusableLatentResolver),
                typeof(LtxRetakeMaskApplicator),
            }.SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly)),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(StageSpec)
                || parameter.ParameterType == typeof(ClipSpec)));
    }
}
