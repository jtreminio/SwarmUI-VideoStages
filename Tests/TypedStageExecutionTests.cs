using System.Reflection;
using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class TypedStageExecutionTests
{
    /// <summary>Every type here is internal, so the default public-only lookup would let a legacy
    /// member come back as private/internal without tripping a single guard below.</summary>
    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void Ltx_stage_execution_has_no_legacy_stage_spec_projection()
    {
        Assert.Null(typeof(StagePlan).GetMethod("ToLegacyStageSpec", AnyMember));
        Assert.DoesNotContain(
            typeof(StageRunner).GetMethods(AnyMember),
            method => method.Name == nameof(StageRunner.RunStage)
                && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(StageSpec));
        Assert.DoesNotContain(
            typeof(StageRunner).GetMethods(AnyMember),
            method => method.Name == "RunLegacyStage");
    }

    [Fact]
    public void Ltx_reference_resolution_and_conditioning_keep_the_typed_reference_plan()
    {
        Type resolver = typeof(LtxClipRefResolver);
        Type resolvedReference = typeof(ResolvedClipRef);

        Assert.Contains(
            resolver.GetMethods(AnyMember),
            method => method.Name == "ResolveStageClipRefs"
                && method.GetParameters()[0].ParameterType == typeof(ClipPlan));
        Assert.Equal(
            typeof(ImageReferencePlan),
            resolvedReference.GetProperty(nameof(ResolvedClipRef.Reference), AnyMember)
                ?.PropertyType);
        Assert.Null(resolvedReference.GetProperty("Spec", AnyMember));
    }

    [Fact]
    public void Ltx_prompt_execution_has_no_legacy_prompt_window_tiler()
    {
        Assert.Null(
            typeof(LtxStageExecutor).Assembly.GetType(
                "VideoStages.Architectures.Ltx2.PromptWindowTiler",
                throwOnError: false));
    }

    [Fact]
    public void Ltx_stage_length_resolution_accepts_the_architecture_resolved_clip_plan()
    {
        MethodInfo resolver = typeof(LtxStageLatentAudioFactory).GetMethod(
            "TryResolveControlNetLengthFrames",
            AnyMember);

        Assert.NotNull(resolver);
        Assert.Equal(typeof(ClipPlan), Assert.Single(resolver.GetParameters()).ParameterType);
    }

    [Fact]
    public void Ltx_latent_collaborators_keep_typed_plan_boundaries()
    {
        MethodInfo retake = typeof(LtxVideoRetakeMasker).GetMethod("ApplyIfActive", AnyMember);
        MethodInfo audioLength = typeof(LtxStageLatentAudioFactory).GetMethod(
            "TryResolveControlNetLengthFrames",
            AnyMember);

        Assert.NotNull(retake);
        Assert.Equal(typeof(RetakePlan), retake.GetParameters()[2].ParameterType);
        Assert.NotNull(audioLength);
        Assert.Equal(typeof(ClipPlan), Assert.Single(audioLength.GetParameters()).ParameterType);
        Assert.DoesNotContain(
            new[]
            {
                typeof(LtxStageLatentBuilder),
                typeof(LtxStageLatentAudioFactory),
                typeof(LtxReusableLatentResolver),
                typeof(LtxVideoRetakeMasker),
            }.SelectMany(type => type.GetMethods(AnyMember | BindingFlags.DeclaredOnly)),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(StageSpec)
                || parameter.ParameterType == typeof(ClipSpec)));
    }
}
