using VideoStages.Planning;
using VideoStages.Execution;
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
}
