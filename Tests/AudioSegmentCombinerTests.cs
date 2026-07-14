using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioSegmentCombinerTests
{
    private static JObject BuildWorkflowWithBaseAudio()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        // Base audio the segments overlay onto.
        bridge.AddStub("StubAudio", "203").WithOutputs(WGNodeData.DT_AUDIO);
        return workflow;
    }

    private static WorkflowGenerator BuildGenerator(JObject workflow) =>
        new()
        {
            UserInput = new(null),
            Features = [],
            Workflow = workflow,
        };

    private static WGNodeData BaseAudio(WorkflowGenerator g) =>
        new(new JArray("203", 0), g, WGNodeData.DT_AUDIO, T2IModelClassSorter.CompatLtxv2);

    private static UploadedAudioSpec Upload(string base64 = "QUJD") =>
        new($"data:audio/wav;base64,{base64}", "seg.wav");

    private static ClipSpec ClipWithSegments(params AudioSegmentSpec[] segments) =>
        new(
            Id: 0,
            Frames: 240,
            AudioSource: Constants.AudioSourceNative,
            ControlNetSource: Constants.ControlNetSourceOne,
            ControlNetLora: "",
            SaveAudioTrack: false,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: false,
            UploadedAudio: null,
            ImageRefs: [],
            Stages: [],
            AudioSegments: segments);

    private static int CountClassType(JObject workflow, string classType)
    {
        int count = 0;
        foreach (JProperty prop in workflow.Properties())
        {
            if (prop.Value is JObject node
                && string.Equals(node["class_type"]?.ToString(), classType, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    [Fact]
    public void Combine_NoSegments_ReturnsBaseUnchanged_NoNewNodes()
    {
        JObject workflow = BuildWorkflowWithBaseAudio();
        WorkflowGenerator g = BuildGenerator(workflow);
        WGNodeData baseAudio = BaseAudio(g);
        int before = workflow.Count;

        WGNodeData result = new AudioSegmentCombiner(g).Combine(
            ClipWithSegments(),
            baseAudio,
            clipDurationSeconds: 10.0);

        Assert.Same(baseAudio, result);
        Assert.Equal(before, workflow.Count);
        Assert.Equal(0, CountClassType(workflow, "AudioMerge"));
        Assert.Equal(0, CountClassType(workflow, "TrimAudioDuration"));
    }

    [Fact]
    public void Combine_OverlaysSegmentsAdditively_WithTrimOffsetAndMerge()
    {
        JObject workflow = BuildWorkflowWithBaseAudio();
        WorkflowGenerator g = BuildGenerator(workflow);
        WGNodeData baseAudio = BaseAudio(g);

        WGNodeData result = new AudioSegmentCombiner(g).Combine(
            ClipWithSegments(
                new AudioSegmentSpec(Upload("QUJD"), StartSeconds: 0.0, TrimStartSeconds: 1.0, LengthSeconds: 3.0),
                new AudioSegmentSpec(Upload("WFla"), StartSeconds: 2.0, TrimStartSeconds: 0.0, LengthSeconds: 3.0)),
            baseAudio,
            clipDurationSeconds: 10.0);

        // Each segment: one upload load + one trim. The offset (start > 0) segment adds one silence + concat.
        Assert.Equal(2, CountClassType(workflow, "SwarmLoadAudioB64"));
        Assert.Equal(2, CountClassType(workflow, "TrimAudioDuration"));
        Assert.Equal(1, CountClassType(workflow, "EmptyAudio"));
        Assert.Equal(1, CountClassType(workflow, "AudioConcat"));
        // Base is audio1; each segment is mixed over the running accumulator -> two merges.
        Assert.Equal(2, CountClassType(workflow, "AudioMerge"));

        // Result points at the final AudioMerge, additively (merge_method="add").
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput output = bridge.ResolvePath(result.Path);
        Assert.NotNull(output);
        Assert.Equal("AudioMerge", output.Node.ClassTypeName);
        Assert.Equal("add", workflow[output.Node.Id]?["inputs"]?["merge_method"]?.ToString());
    }

    [Fact]
    public void Combine_NoBaseAudio_SynthesizesSilentBed_ForOffsetPlacement()
    {
        JObject workflow = [];
        WorkflowGenerator g = BuildGenerator(workflow);

        WGNodeData result = new AudioSegmentCombiner(g).Combine(
            ClipWithSegments(
                new AudioSegmentSpec(Upload(), StartSeconds: 2.0, TrimStartSeconds: 0.0, LengthSeconds: 3.0)),
            baseAudio: null,
            clipDurationSeconds: 10.0);

        Assert.NotNull(result);
        // Silent clip-duration bed + silence for the segment offset = 2 EmptyAudio nodes.
        Assert.Equal(2, CountClassType(workflow, "EmptyAudio"));
        Assert.Equal(1, CountClassType(workflow, "AudioMerge"));
    }
}
