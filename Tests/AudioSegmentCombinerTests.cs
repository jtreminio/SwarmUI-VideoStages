using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;
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

    private static AudioMediaIdentityPlan Upload(string base64 = "QUJD") =>
        new($"data:audio/wav;base64,{base64}", "seg.wav");

    private static AudioSegmentItemPlan UploadSegment(
        AudioMediaIdentityPlan source,
        double StartSeconds,
        double TrimStartSeconds,
        double LengthSeconds,
        double Volume = 1) =>
        new(
            AudioSegmentSourceKind.Upload,
            AceStepFunTrack: null,
            StartSeconds,
            TrimStartSeconds,
            LengthSeconds,
            source,
            Volume);

    private static AudioSegmentItemPlan AceSegment(
        int track,
        double StartSeconds,
        double TrimStartSeconds,
        double LengthSeconds) =>
        new(
            AudioSegmentSourceKind.AceStepFun,
            track,
            StartSeconds,
            TrimStartSeconds,
            LengthSeconds,
            UploadedMedia: null);

    private static AudioSegmentPlan SegmentPlan(params AudioSegmentItemPlan[] segments) =>
        AudioSegmentPlanCompiler.Compile(
            segments,
            new AudioBaseSourcePlan(
                AudioBaseSourceKind.Native,
                Constants.AudioSourceNative,
                AceStepFunTrack: null,
                HasConfiguredTrack: true,
                UploadedMedia: null)).Plan;

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
            0,
            SegmentPlan(),
            baseAudio,
            clipDurationSeconds: 10.0,
            out _);

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
            0,
            SegmentPlan(
                UploadSegment(
                    Upload("QUJD"),
                    StartSeconds: 0.0,
                    TrimStartSeconds: 1.0,
                    LengthSeconds: 3.0,
                    Volume: 0.5),
                UploadSegment(Upload("WFla"), StartSeconds: 2.0, TrimStartSeconds: 0.0, LengthSeconds: 3.0)),
            baseAudio,
            clipDurationSeconds: 10.0,
            out _);

        // Each segment: one upload load + one trim. The offset (start > 0) segment adds one silence + concat.
        Assert.Equal(2, CountClassType(workflow, "SwarmLoadAudioB64"));
        Assert.Equal(2, CountClassType(workflow, "TrimAudioDuration"));
        Assert.Equal(1, CountClassType(workflow, "EmptyAudio"));
        Assert.Equal(1, CountClassType(workflow, "AudioConcat"));
        Assert.Equal(1, CountClassType(workflow, "AudioAdjustVolume"));
        // Base is audio1; each segment is mixed over the running accumulator -> two merges.
        Assert.Equal(2, CountClassType(workflow, "AudioMerge"));

        // Result points at the final AudioMerge, additively (merge_method="add").
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput output = bridge.ResolvePath(result.Path);
        Assert.NotNull(output);
        Assert.Equal("AudioMerge", output.Node.ClassTypeName);
        Assert.Equal("add", workflow[output.Node.Id]?["inputs"]?["merge_method"]?.ToString());

        // AudioConcat only turns silence + the offset segment into one positioned overlay track.
        // That positioned track is then connected to AudioMerge.audio2, never appended to the base.
        AudioConcatNode placement = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>());
        Assert.IsType<EmptyAudioNode>(placement.Audio1.Connection!.Node);
        Assert.IsType<TrimAudioDurationNode>(placement.Audio2.Connection!.Node);
        AudioAdjustVolumeNode adjusted = Assert.Single(
            bridge.Graph.NodesOfType<AudioAdjustVolumeNode>());
        Assert.Equal(-6, adjusted.Volume.LiteralAsInt());
        Assert.IsType<TrimAudioDurationNode>(adjusted.Audio.Connection!.Node);
        Assert.Contains(
            bridge.Graph.NodesOfType<AudioMergeNode>(),
            merge => ReferenceEquals(merge.Audio2.Connection, adjusted.AUDIO));
        Assert.Contains(
            bridge.Graph.NodesOfType<AudioMergeNode>(),
            merge => ReferenceEquals(merge.Audio2.Connection, placement.AUDIO));
    }

    [Fact]
    public void Combine_ReportsLoadedSegmentWindows()
    {
        JObject workflow = BuildWorkflowWithBaseAudio();
        WorkflowGenerator g = BuildGenerator(workflow);
        WGNodeData baseAudio = BaseAudio(g);

        _ = new AudioSegmentCombiner(g).Combine(
            0,
            SegmentPlan(
                UploadSegment(Upload("QUJD"), StartSeconds: 0.5, TrimStartSeconds: 0.0, LengthSeconds: 3.0),
                UploadSegment(Upload("WFla"), StartSeconds: 6.0, TrimStartSeconds: 0.0, LengthSeconds: 2.0)),
            baseAudio,
            clipDurationSeconds: 10.0,
            out IReadOnlyList<(double Start, double End)> windows);

        Assert.Equal([(0.5, 3.5), (6.0, 8.0)], windows);
    }

    [Fact]
    public void Combine_MapsFullVolumeMultiplierRangeToComfyDb()
    {
        JObject workflow = BuildWorkflowWithBaseAudio();
        WorkflowGenerator g = BuildGenerator(workflow);

        _ = new AudioSegmentCombiner(g).Combine(
            0,
            SegmentPlan(
                UploadSegment(Upload("MDE="), 0, 0, 1, Volume: 0.00001),
                UploadSegment(Upload("MDI="), 1, 0, 1, Volume: 1),
                UploadSegment(Upload("MDM="), 2, 0, 1, Volume: 4),
                UploadSegment(Upload("MDQ="), 3, 0, 1, Volume: 100000)),
            BaseAudio(g),
            clipDurationSeconds: 10,
            out _);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        int[] adjustments = bridge.Graph.NodesOfType<AudioAdjustVolumeNode>()
            .Select(node => node.Volume.LiteralAsInt()!.Value)
            .Order()
            .ToArray();
        Assert.Equal([-100, 12, 100], adjustments);
    }

    [Fact]
    public void Combine_NoMaterializableSegments_ReportsNoWindows()
    {
        JObject workflow = BuildWorkflowWithBaseAudio();
        WorkflowGenerator g = BuildGenerator(workflow);
        WGNodeData baseAudio = BaseAudio(g);

        WGNodeData result = new AudioSegmentCombiner(g).Combine(
            0,
            SegmentPlan(AceSegment(
                track: 5, StartSeconds: 0.0, TrimStartSeconds: 0.0, LengthSeconds: 3.0)),
            baseAudio,
            clipDurationSeconds: 10.0,
            out IReadOnlyList<(double Start, double End)> windows);

        Assert.Same(baseAudio, result);
        Assert.Empty(windows);
    }

    [Fact]
    public void Combine_AceStepFunSegment_UsesDecodeNodeAudio_NoLoadNode()
    {
        JObject workflow = [];
        using (WorkflowBridge setup = WorkflowBridge.Create(workflow))
        {
            // AceStepFun decode node for track 2 (id = 65160 + 2*100) + the base audio stub.
            setup.AddStub("VAEDecodeAudio", "65360").WithOutputs(WGNodeData.DT_AUDIO);
            setup.AddStub("StubAudio", "203").WithOutputs(WGNodeData.DT_AUDIO);
        }
        WorkflowGenerator g = BuildGenerator(workflow);
        WGNodeData baseAudio = BaseAudio(g);

        AudioSegmentPlan segmentPlan = new(
            [new AudioSegmentItemPlan(
                AudioSegmentSourceKind.AceStepFun,
                AceStepFunTrack: 2,
                StartSeconds: 2.0,
                TrimStartSeconds: 0.0,
                LengthSeconds: 3.0,
                UploadedMedia: null)]);
        WGNodeData result = new AudioSegmentCombiner(g).Combine(
            0,
            segmentPlan,
            baseAudio,
            clipDurationSeconds: 10.0,
            out _);

        Assert.NotNull(result);
        // No upload load node — the segment reads straight from the decode node, via the length pad.
        Assert.Equal(0, CountClassType(workflow, "SwarmLoadAudioB64"));
        Assert.Equal(1, CountClassType(workflow, "TrimAudioDuration"));
        Assert.Equal(1, CountClassType(workflow, "AudioMerge"));
        JObject ensure = workflow.Properties()
            .Select(p => p.Value as JObject)
            .Single(node => node?["class_type"]?.ToString() == "SwarmEnsureAudio");
        Assert.Equal("65360", (ensure["inputs"]?["audio"] as JArray)?[0]?.ToString());
        string ensureId = workflow.Properties()
            .Single(p => ReferenceEquals(p.Value, ensure)).Name;
        JObject trim = workflow.Properties()
            .Select(p => p.Value as JObject)
            .Single(node => node?["class_type"]?.ToString() == "TrimAudioDuration");
        Assert.Equal(ensureId, (trim["inputs"]?["audio"] as JArray)?[0]?.ToString());
    }

    [Fact]
    public void Combine_AceStepFunSegment_MissingDecodeNode_IsSkipped()
    {
        JObject workflow = BuildWorkflowWithBaseAudio();
        WorkflowGenerator g = BuildGenerator(workflow);
        WGNodeData baseAudio = BaseAudio(g);
        int before = workflow.Count;

        WGNodeData result = new AudioSegmentCombiner(g).Combine(
            0,
            SegmentPlan(AceSegment(
                track: 5, StartSeconds: 0.0, TrimStartSeconds: 0.0, LengthSeconds: 3.0)),
            baseAudio,
            clipDurationSeconds: 10.0,
            out _);

        Assert.Same(baseAudio, result);
        Assert.Equal(before, workflow.Count);
    }

    [Fact]
    public void Combine_NoBaseAudio_SynthesizesSilentBed_ForOffsetPlacement()
    {
        JObject workflow = [];
        WorkflowGenerator g = BuildGenerator(workflow);

        WGNodeData result = new AudioSegmentCombiner(g).Combine(
            0,
            SegmentPlan(
                UploadSegment(Upload(), StartSeconds: 2.0, TrimStartSeconds: 0.0, LengthSeconds: 3.0)),
            baseAudio: null,
            clipDurationSeconds: 10.0,
            out _);

        Assert.NotNull(result);
        // Silent clip-duration bed + silence for the segment offset = 2 EmptyAudio nodes.
        Assert.Equal(2, CountClassType(workflow, "EmptyAudio"));
        Assert.Equal(1, CountClassType(workflow, "AudioMerge"));
    }
}
