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
    private static string ClipWithDurationJson(double duration, params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        clip["duration"] = duration;
        return new JArray(clip).ToString();
    }

    // Prompt windows now come from <videoclip[clip]:start-end[,skip]>text tags, not JSON.
    private static string ClipWindowTag(string prompt, double start, double duration, int clip = 0) =>
        $"<videoclip[{clip}]:{start.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        + $"-{(start + duration).ToString(System.Globalization.CultureInfo.InvariantCulture)}>{prompt}";

    [Fact]
    public void Native_ltx_clip_with_a_minor_window_and_gap_emits_a_wired_prompt_relay()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // One short MINOR window over the opening of the clip; the remainder is a gap that the node
        // fills with the global (MAJOR) prompt — two tiled windows, so the relay activates. The window
        // duration is kept well under the planned clip length so a gap is guaranteed. The runtime
        // must use the compiler's aligned 97-frame prompt plan, not the harness's 16-frame host input.
        string stagesJson = ClipWithDurationJson(
            duration: 4.0,
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, stagesJson,
            prompt: $"global words {ClipWindowTag("a red car", start: 0.0, duration: 0.25)}");
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
        Assert.True(
            windows.Sum(window => (double)window["seconds"]) > 3.5,
            "Prompt relay windows must retain the compiled clip duration.");

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

        string stagesJson = ClipWithDurationJson(
            duration: 4.0,
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
        string stagesJson = ClipWithDurationJson(
            duration: 4.0,
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, stagesJson,
            prompt: $"global words {ClipWindowTag("whole clip", start: 0.0, duration: 5.0)}");
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
