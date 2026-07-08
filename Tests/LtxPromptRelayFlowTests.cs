using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    private static string ClipWithPromptWindowsJson(
        double duration,
        IEnumerable<JObject> promptWindows,
        params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        clip["Duration"] = duration;
        clip["PromptWindows"] = new JArray(promptWindows);
        return new JArray(clip).ToString();
    }

    private static JObject MakePromptWindow(string prompt, double start, double duration) =>
        new()
        {
            ["Prompt"] = prompt,
            ["Start"] = start,
            ["Duration"] = duration,
        };

    [Fact]
    public void Native_ltx_clip_with_a_minor_window_and_gap_emits_a_wired_prompt_relay()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // One short MINOR window over the opening of the clip; the remainder is a gap that the node
        // fills with the global (MAJOR) prompt — two tiled windows, so the relay activates. The window
        // duration is kept well under the harness clip length (VideoFrames 16 @ 24fps ≈ 0.67s) so a gap
        // is guaranteed. The tiling is measured against the actual generated frame count, not Duration.
        string stagesJson = ClipWithPromptWindowsJson(
            duration: 4.0,
            promptWindows: [MakePromptWindow("a red car", start: 0.0, duration: 0.25)],
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, stagesJson, prompt: "global words");
        (JObject workflow, WorkflowGenerator _unused) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmPromptRelayEncodeNode relay = Assert.Single(
            bridge.Graph.NodesOfType<SwarmPromptRelayEncodeNode>());

        Assert.Equal("global words", relay.GlobalPrompt.LiteralAsString());
        Assert.Equal(0.001, relay.Epsilon.LiteralAsDouble());
        Assert.NotNull(relay.ModelInput.Connection);
        Assert.NotNull(relay.Clip.Connection);

        // The tiled window list leads with the MINOR prompt, then a blank gap window.
        JArray windows = JArray.Parse(relay.Windows.LiteralAsString());
        Assert.Equal("a red car", (string)windows[0]["prompt"]);
        Assert.True((double)windows[0]["seconds"] > 0);
        Assert.True(windows.Count >= 2);
        Assert.Equal("", (string)windows[^1]["prompt"]);

        // The positive conditioning is taken from the relay's positive output (slot 1).
        LTXVConditioningNode conditioning = Assert.Single(bridge.Graph.NodesOfType<LTXVConditioningNode>());
        Assert.Equal(relay.Id, conditioning.PositiveInput.Connection!.Node.Id);
        Assert.Equal(1, conditioning.PositiveInput.Connection.SlotIndex);
        // Negative still comes from a plain advanced text encoder.
        Assert.IsType<SwarmClipTextEncodeAdvancedNode>(conditioning.NegativeInput.Connection!.Node);
    }

    [Fact]
    public void Native_ltx_clip_with_no_prompt_windows_emits_no_relay()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = ClipWithPromptWindowsJson(
            duration: 4.0,
            promptWindows: [],
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, stagesJson, prompt: "global words");
        (JObject workflow, WorkflowGenerator _unused) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmPromptRelayEncodeNode>());
    }

    [Fact]
    public void Native_ltx_clip_with_a_single_full_span_window_uses_its_prompt_without_a_relay()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // A window covering the whole clip tiles to a single segment — below the two-window gate — so
        // no relay node is built, but its prompt still replaces the MAJOR prompt on the plain encode.
        string stagesJson = ClipWithPromptWindowsJson(
            duration: 4.0,
            promptWindows: [MakePromptWindow("whole clip", start: 0.0, duration: 4.0)],
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, stagesJson, prompt: "global words");
        (JObject workflow, WorkflowGenerator _unused) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmPromptRelayEncodeNode>());

        LTXVConditioningNode conditioning = Assert.Single(bridge.Graph.NodesOfType<LTXVConditioningNode>());
        SwarmClipTextEncodeAdvancedNode positive =
            (SwarmClipTextEncodeAdvancedNode)conditioning.PositiveInput.Connection!.Node;
        Assert.Equal("whole clip", positive.Prompt.LiteralAsString());
    }
}
