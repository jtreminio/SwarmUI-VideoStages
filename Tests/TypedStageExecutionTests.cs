using System.Reflection;
using VideoStages.Planning;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime;
using VideoStages.Architectures.Ltx2.Runtime.Guide;
using VideoStages.Architectures.Ltx2.Runtime.Stage;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class TypedStageExecutionTests
{
    /// <summary>The probed members are all instance, and a mix of public and internal.</summary>
    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void Ltx_reference_resolution_and_conditioning_keep_the_typed_reference_plan()
    {
        Type resolver = typeof(FrameRefResolver);
        Type resolvedReference = typeof(ResolvedFrameRef);

        Assert.Contains(
            resolver.GetMethods(AnyMember),
            method => method.Name == "ResolveStageFrameRefs"
                && method.GetParameters()[0].ParameterType == typeof(ClipPlan));
        Assert.Equal(
            typeof(FrameRefPlan),
            resolvedReference.GetProperty(nameof(ResolvedFrameRef.Reference), AnyMember)
                ?.PropertyType);
    }

    [Fact]
    public void Ltx_latent_collaborators_keep_typed_plan_boundaries()
    {
        MethodInfo retake = typeof(LtxVideoRetakeMasker).GetMethod("ApplyIfActive", AnyMember);
        MethodInfo clipLength = typeof(LtxControlNetMediaNormalizer).GetMethod(
            "CreateClipLengthFrames",
            AnyMember);

        Assert.NotNull(retake);
        Assert.Equal(typeof(RetakePlan), retake.GetParameters()[2].ParameterType);
        Assert.NotNull(clipLength);
        Assert.Equal(typeof(ClipPlan), clipLength.GetParameters()[0].ParameterType);
    }
}
