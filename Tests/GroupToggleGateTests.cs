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
    // The VideoStages config rides in the prompt as a <videostages> JSON section, which is always sent even
    // when the VideoStages param group is toggled off. Turning the group off makes SwarmUI omit the group's
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
        // <videostages> section is still in the prompt, but no stages should be built.
        T2IParamInput disabled = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        disabled.Remove(VideoStagesExtension.Enabled);
        JObject disabledWorkflow = WorkflowTestHarness
            .GenerateWithStepsAndState(disabled, BuildNativeSteps(attachAudioToCurrentMedia: false))
            .Workflow;
        using WorkflowBridge disabledBridge = WorkflowBridge.Create(disabledWorkflow);
        Assert.Empty(disabledBridge.Graph.NodesOfType<LTXVConditioningNode>());
    }
}
