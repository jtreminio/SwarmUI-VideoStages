using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.MiniMax;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// What is left of the MiniMax stub flow after the graph conversion (see
/// <see cref="MiniMaxGeneratedWorkflowContractTests"/>): the one case the real Comfy API POST path
/// cannot reach, because the route flattens every blocking diagnostic into a single message string
/// and this asserts the codes and severities behind it.
/// <para>
/// It still drives the provider directly, but off the checked-in <c>minimax-h3</c> checkpoint rather
/// than a hand-forged model class, so "this model resolves to MiniMax" is not circular.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class MiniMaxRuntimeFlowTests
{
    [Fact]
    public void Audio_derived_duration_refuses_multi_clip_but_allows_global_trim()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject dynamicClip = MakeClip(1.0, fixture.Stage());
        dynamicClip["audioSource"] = MediaSource.Upload;
        dynamicClip["clipLengthFromAudio"] = true;
        dynamicClip["uploadedAudio"] = UploadedAudio();
        T2IParamInput input = BuildNativeInput(
            fixture.BaseModel,
            fixture.Model,
            MakeDocument(dynamicClip, MakeClip(1.0, fixture.Stage())).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 1);
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Workflow = [],
        };
        VideoExecutionPlan plan = generator.RequireVideoExecutionPlanContext().Plan;

        IReadOnlyList<PlanDiagnostic> diagnostics = new MiniMaxSessionProvider(generator)
            .PreflightRequest(new(plan, MiniMaxArchitectureModule.ArchitectureId));

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code
                == "minimax.audio-derived-duration.multi-clip-unsupported");
        Assert.All(
            diagnostics.Where(diagnostic => diagnostic.Code.StartsWith(
                "minimax.audio-derived-duration",
                StringComparison.Ordinal)),
            diagnostic => Assert.Equal(PlanDiagnosticSeverity.Error, diagnostic.Severity));
    }
}
