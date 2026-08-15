using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.MiniMax;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// What is left of the MiniMax stub flow after the graph conversion (see
/// <see cref="MiniMaxGeneratedWorkflowContractTests"/>): the preflight diagnostics, which the real
/// Comfy API POST path cannot reach because the route flattens them into a single message string.
/// This asserts the codes and severities behind it.
/// <para>
/// It still drives the provider directly, but off the checked-in <c>minimax-h3</c> checkpoint rather
/// than a hand-forged model class, so "this model resolves to MiniMax" is not circular.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class MiniMaxRuntimeFlowTests
{
    [Fact]
    public void Audio_derived_duration_refuses_multi_clip()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject dynamicClip = MakeClip(1.0, fixture.Stage());
        dynamicClip["audioSource"] = MediaSource.Upload;
        dynamicClip["clipLengthFromAudio"] = true;
        dynamicClip["uploadedAudio"] = UploadedAudio();

        IReadOnlyList<PlanDiagnostic> diagnostics = Preflight(
            Request(fixture, dynamicClip, MakeClip(1.0, fixture.Stage())));

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

    [Fact]
    public void A_host_creativity_setting_is_warned_as_unsupported()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        T2IParamInput input = Request(fixture, MakeClip(1.0, fixture.Stage()));
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.6);

        PlanDiagnostic warning = Assert.Single(Preflight(input));

        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("minimax.host-param.unsupported", warning.Code);
    }

    /// <summary>
    /// The global end frame has a single-clip fallback (see
    /// <see cref="MiniMaxReferenceConditioningContractTests"/>), so a second clip is what makes its
    /// target ambiguous.
    /// </summary>
    [Fact]
    public void A_global_end_frame_is_warned_away_on_a_multi_clip_timeline()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        Image endFrame = new([0x01], MediaType.ImagePng);
        T2IParamInput single = Request(fixture, MakeClip(1.0, fixture.Stage()));
        single.Set(T2IParamTypes.VideoEndImage, endFrame);
        T2IParamInput multi = Request(
            fixture,
            MakeClip(1.0, fixture.Stage()),
            MakeClip(1.0, fixture.Stage()));
        multi.Set(T2IParamTypes.VideoEndImage, endFrame);

        Assert.Empty(Preflight(single));
        PlanDiagnostic warning = Assert.Single(Preflight(multi));

        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("minimax.end-frame.ignored", warning.Code);
    }

    private static T2IParamInput Request(
        MiniMaxWorkflowFixture fixture,
        params JObject[] clips) =>
        BuildNativeInput(fixture.BaseModel, fixture.Model, MakeDocument(clips).ToString());

    private static IReadOnlyList<PlanDiagnostic> Preflight(T2IParamInput input)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Workflow = [],
        };
        return new MiniMaxSessionProvider(generator).PreflightRequest(
            new(generator.RequireVideoExecutionPlanContext().Plan,
                MiniMaxArchitectureModule.ArchitectureId));
    }
}
