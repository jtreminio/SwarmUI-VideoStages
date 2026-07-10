using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    // The VideoStages config rides in the hidden "Video Stages Data" param, which is always sent even when
    // the VideoStages param group is toggled off. Turning the group off makes SwarmUI omit the group's
    // parameters (notably the Enabled gate flag), and that absence must make the extension fully inert.
    [Fact]
    public void Section_present_but_group_toggle_off_does_not_activate_video_stages()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        // Group ON (baseline): the configured stage builds its own LTXV conditioning node.
        T2IParamInput enabled = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        JObject enabledWorkflow = WorkflowTestHarness
            .GenerateWithStepsAndState(enabled, BuildNativeSteps(attachAudioToCurrentMedia: false))
            .Workflow;
        using (WorkflowBridge enabledBridge = WorkflowBridge.Create(enabledWorkflow))
        {
            Assert.NotEmpty(enabledBridge.Graph.NodesOfType<LTXVConditioningNode>());
        }

        // Group OFF: the Enabled gate flag is absent, exactly as SwarmUI sends a toggled-off group. The identical
        // config JSON is still present in the Data param, but no stages should be built.
        T2IParamInput disabled = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        disabled.Remove(VideoStagesExtension.Enabled);
        JObject disabledWorkflow = WorkflowTestHarness
            .GenerateWithStepsAndState(disabled, BuildNativeSteps(attachAudioToCurrentMedia: false))
            .Workflow;
        using WorkflowBridge disabledBridge = WorkflowBridge.Create(disabledWorkflow);
        Assert.Empty(disabledBridge.Graph.NodesOfType<LTXVConditioningNode>());
    }

    // Prompt-tag dimension overrides (<videostages[width]:...>) ride ExtraMeta, which the global prompt
    // processor populates even when the VideoStages group is toggled off. The root resolver must ignore them
    // in that case, otherwise a disabled extension would still force a root resize.
    [Fact]
    public void Dimension_override_tags_do_not_resolve_root_size_when_group_toggle_off()
    {
        using SwarmUiTestContext _ = new();
        string json = MakeRootConfig(800, 600).ToString();
        const string prompt = "cat <videostages[width]:1024> <videostages[height]:1024>";

        // Group ON: the tag overrides win over the JSON root dimensions.
        T2IParamInput enabled = new(null);
        SetVideoStagesConfig(enabled, json);
        enabled.Set(T2IParamTypes.Prompt, prompt);
        enabled.ApplyLateSpecialLogic();
        (int? onWidth, int? onHeight) = VideoStagesSpecParser.GetRawJsonTopLevelDimensions(
            new() { UserInput = enabled });
        Assert.Equal(1024, onWidth);
        Assert.Equal(1024, onHeight);

        // Group OFF: the Enabled gate flag is absent (as SwarmUI sends a toggled-off group). The identical
        // override tags are still stashed in ExtraMeta, but no root dimensions must be resolved.
        T2IParamInput disabled = new(null);
        SetVideoStagesConfig(disabled, json);
        disabled.Set(T2IParamTypes.Prompt, prompt);
        disabled.ApplyLateSpecialLogic();
        disabled.Remove(VideoStagesExtension.Enabled);
        (int? offWidth, int? offHeight) = VideoStagesSpecParser.GetRawJsonTopLevelDimensions(
            new() { UserInput = disabled });
        Assert.Null(offWidth);
        Assert.Null(offHeight);
    }
}
