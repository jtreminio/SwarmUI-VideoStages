using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Execution;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Graph-shape tests for multi-clip cut and overlap assembly without a full LTX flow.
/// </summary>
[Collection("VideoStagesTests")]
public class MultiClipCrossfadeMergerTests
{
    private const int Fps = 24;

    private static string VideoId(int i) => $"{10 + i}";
    private static string AudioId(int i) => $"{40 + i}";

    private static WorkflowGenerator NewGenerator(JObject workflow)
    {
        // Registers the node types required for typed workflow deserialization.
        _ = WorkflowTestHarness.VideoStagesSteps();
        return new WorkflowGenerator
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            Workflow = workflow,
        };
    }

    private static (WorkflowGenerator Generator, List<WGNodeData> Clips) BuildClips(
        int[] frames,
        T2IModelCompatClass compat,
        int[] widths = null,
        int height = 512,
        int[] heights = null,
        int[] framesPerSecond = null)
    {
        JObject workflow = [];
        WorkflowGenerator g = NewGenerator(workflow);
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            for (int i = 0; i < frames.Length; i++)
            {
                bridge.AddStub("UnitTest_ClipVideo", VideoId(i)).WithOutputs(WGNodeData.DT_IMAGE);
                bridge.AddStub("UnitTest_ClipAudio", AudioId(i)).WithOutputs(WGNodeData.DT_AUDIO);
            }
        }

        List<WGNodeData> clips = [];
        for (int i = 0; i < frames.Length; i++)
        {
            clips.Add(new WGNodeData(new JArray(VideoId(i), 0), g, WGNodeData.DT_VIDEO, compat)
            {
                Width = widths is null ? 512 : widths[i],
                Height = heights is null ? height : heights[i],
                Frames = frames[i],
                FPS = new JValue(framesPerSecond is null ? Fps : framesPerSecond[i]),
                AttachedAudio = new WGNodeData(new JArray(AudioId(i), 0), g, WGNodeData.DT_AUDIO, compat),
            });
        }
        return (g, clips);
    }

    private static int CountOf<T>(WorkflowBridge bridge) where T : ComfyNode =>
        bridge.Graph.NodesOfType<T>().Count;

    private static MultiClipParallelMerger Merger(WorkflowGenerator generator) =>
        new(
            generator,
            new Dictionary<ArchitectureId, IArchitectureBoundaryAssembler>
            {
                [Ltx2ArchitectureModule.ArchitectureId] = new Ltx2BoundaryAssembler(),
            });

    private static List<DecodedClipArtifact> Artifacts(
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<ArchitectureId> architectures = null)
    {
        List<DecodedClipArtifact> artifacts = [];
        for (int i = 0; i < clips.Count; i++)
        {
            WGNodeData clip = clips[i];
            JArray video = Assert.IsType<JArray>(clip.Path);
            JArray audio = clip.AttachedAudio?.Path as JArray;
            artifacts.Add(new(
                new(video[0].ToString(), video[1].Value<int>(), DecodedMediaKind.Video),
                audio is null
                    ? null
                    : new(audio[0].ToString(), audio[1].Value<int>(), DecodedMediaKind.Audio),
                clip.Width!.Value,
                clip.Height!.Value,
                clip.GetRawFPS()!.Value,
                clip.Frames!.Value,
                architectures?[i] ?? Ltx2ArchitectureModule.ArchitectureId,
                i));
        }
        return artifacts;
    }

    private static IReadOnlyList<BoundaryPlan> PlansFor(
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<string> boundaryOuts,
        IReadOnlyList<int> continueWindows = null,
        IReadOnlyList<int> boundaryOverlapPrefs = null)
    {
        IReadOnlyList<int> fixtureContinueWindows = continueWindows
            ?? Enumerable.Repeat(1, Math.Max(0, clips.Count - 1)).ToArray();
        return BoundaryPlanFixture.Resolve(
            [.. clips.Select(clip => clip?.Frames)],
            boundaryOuts,
            boundaryOverlapPrefs,
            fixtureContinueWindows).Boundaries;
    }

    [Fact]
    public void ExplicitMergeReturnsArtifactWithoutPublishingHostMedia()
    {
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);

        TimelineMergeResult result =
            Merger(g).Merge(Artifacts(clips), PlansFor(clips, ["cut"]));

        Assert.NotNull(result.Artifact);
        Assert.True(result.Artifact.HasMedia);
        Assert.Equal(34, result.Artifact.Media.Frames);
        Assert.NotNull(result.Artifact.Media.AttachedAudio);
        Assert.Null(g.CurrentMedia);
    }

    [Fact]
    public void Cut_MultiClip_ProducesExactlyTodaysGraphShape_NoCrossfadeNodes()
    {
        // Explicit cuts must preserve the graph produced when no boundary preferences are supplied.
        (WorkflowGenerator gA, List<WGNodeData> clipsA) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        Merger(gA).Apply(Artifacts(clipsA), PlansFor(clipsA, null));

        (WorkflowGenerator gB, List<WGNodeData> clipsB) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        Merger(gB).Apply(Artifacts(clipsB), PlansFor(clipsB, ["cut", "cut", "cut"]));

        Assert.True(JToken.DeepEquals(gA.Workflow, gB.Workflow),
            "All-cut boundaryOuts must produce the identical graph to the pre-Stage-D signature.");

        using WorkflowBridge bridge = WorkflowBridge.Create(gB.Workflow);
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(2, CountOf<AudioConcatNode>(bridge));
        Assert.Equal(0, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(0, CountOf<ImageFromBatchNode>(bridge));
        Assert.Equal(0, CountOf<SwarmRampMaskBatchNode>(bridge));
        Assert.Equal(0, CountOf<TrimAudioDurationNode>(bridge));
        Assert.Equal(51, gB.CurrentMedia.Frames);
    }

    [Fact]
    public void Cut_MixedAudioAndSilentClips_PadsSilenceAndKeepsTimelineAudio()
    {
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([24, 48], T2IModelClassSorter.CompatLtxv2);
        clips[1].AttachedAudio = null;

        Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        EmptyAudioNode silence = Assert.Single(bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(2.0, silence.Duration.LiteralAsDouble()!.Value, 6);
        AudioConcatNode concat = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());
        Assert.Equal(AudioId(0), concat.Audio1.Connection!.Node.Id);
        Assert.Equal(silence.Id, concat.Audio2.Connection!.Node.Id);
        Assert.Equal(new JArray(concat.Id, 0), g.CurrentMedia.AttachedAudio!.Path);
    }

    [Fact]
    public void Cut_UnresolvedClipVideo_FailsClosedInsteadOfPublishingASubset()
    {
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([24, 24], T2IModelClassSorter.CompatLtxv2);
        using (WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow))
        {
            bridge.RemoveNode(VideoId(1));
        }

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["cut"])));

        Assert.Contains("only 1 of 2 planned clip video outputs", error.Message);
        Assert.Null(g.CurrentMedia);
    }

    [Fact]
    public void Cut_UnresolvedLaterAudio_FailsBeforeAnyTimelineGraphMutation()
    {
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([24, 24], T2IModelClassSorter.CompatLtxv2);
        List<DecodedClipArtifact> artifacts = Artifacts(clips);
        artifacts[1] = artifacts[1] with
        {
            Audio = new("missing-audio", 0, DecodedMediaKind.Audio),
        };
        JObject before = (JObject)g.Workflow.DeepClone();

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => Merger(g).Apply(artifacts, PlansFor(clips, ["cut"])));

        Assert.Contains("decoded audio could not be resolved", error.Message);
        Assert.True(JToken.DeepEquals(before, g.Workflow));
        Assert.Null(g.CurrentMedia);
    }

    [Fact]
    public void Continue_WithoutWindows_DefaultsToOneFrameBlendAndAudioTrim()
    {
        // No continueWindows supplied: an armed "continue" boundary falls back to the conservative
        // 1-frame window — a 50/50 blend of the two duplicated seam frames — and trims clip 0's audio
        // by that frame to stay in sync.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["continue", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);

        // One 1-frame blend at the seam: cores (2) + tail/head slices (2) feed one BatchImages concat.
        LTXVLaplacianPyramidBlendNode blend =
            Assert.Single(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(4, CountOf<ImageFromBatchNode>(bridge));
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));

        // K==1 window: a single one-frame ramp mask (the node emits 0.5 for a lone frame).
        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(1, ramp.Frames.LiteralAsInt());
        Assert.Equal(ramp.Id, blend.Mask.Connection!.Node.Id);

        // Clip 0's audio drops its final frame; clip 1's audio is untouched.
        TrimAudioDurationNode trim = Assert.Single(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal((17 - 1) / (double)Fps, trim.Duration.LiteralAsDouble()!.Value, 6);
        Assert.Equal(AudioId(0), trim.Audio.Connection!.Node.Id);

        Assert.Equal(33, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Continue_WithResolvedWindow_CollapsesTheOverlapWindow()
    {
        // Production path: StageSequenceRunner resolved the default overlap 8 to a 9-frame window
        // (overlap+1) and passes it in. The merge blends the 9 duplicated frames with a full ramp and
        // trims clip 0's audio by the window.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        BoundaryBudgetResolution boundaries = BoundaryPlanFixture.Resolve(
            [17, 17], ["continue", "cut"], [8, 8], [9]);
        Assert.Equal(9, boundaries.Boundaries[0].ContinuityWindowFrames);
        Merger(g).Apply(Artifacts(clips), boundaries.Boundaries);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        const int k = 9;

        LTXVLaplacianPyramidBlendNode blend =
            Assert.Single(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(4, CountOf<ImageFromBatchNode>(bridge));
        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(k, ramp.Frames.LiteralAsInt());
        Assert.Equal(ramp.Id, blend.Mask.Connection!.Node.Id);

        TrimAudioDurationNode trim = Assert.Single(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal((17 - k) / (double)Fps, trim.Duration.LiteralAsDouble()!.Value, 6);
        Assert.Equal(AudioId(0), trim.Audio.Connection!.Node.Id);

        Assert.Equal(34 - k, g.CurrentMedia.Frames);
    }

    [Fact]
    public void ContinueAndCrossfade_MixedBoundaries_CutOnlyTheUnderfundedCrossfade()
    {
        // Boundary 0 is an armed "continue" (window 9). The adjacent crossfade cannot retain its
        // architecture minimum after that window, so planning cuts only that boundary.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        BoundaryBudgetResolution boundaries = BoundaryPlanFixture.Resolve(
            [17, 17, 17], ["continue", "crossfade", "cut"], [8, 8, 8], [9, 0]);
        Assert.Equal(9, boundaries.Boundaries[0].ContinuityWindowFrames);
        Assert.Equal(BoundaryJoinType.Cut, boundaries.Boundaries[1].Effective);
        Merger(g).Apply(Artifacts(clips), boundaries.Boundaries);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        const int contK = 9;

        // Only the funded continue boundary blends; its run hard-cuts to clip 2.
        Assert.Single(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(4, CountOf<ImageFromBatchNode>(bridge));

        SwarmRampMaskBatchNode ramp =
            Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(contK, ramp.Frames.LiteralAsInt());

        TrimAudioDurationNode trim =
            Assert.Single(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(AudioId(0), trim.Audio.Connection!.Node.Id);
        Assert.Equal((17 - contK) / (double)Fps, trim.Duration.LiteralAsDouble()!.Value, 6);

        Assert.Equal(51 - contK, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_Run_IsArchitectureOwned_ThenHardCutToAnotherArchitecture()
    {
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        List<DecodedClipArtifact> artifacts = Artifacts(
            clips,
            [
                Ltx2ArchitectureModule.ArchitectureId,
                Ltx2ArchitectureModule.ArchitectureId,
                new ArchitectureId("fake"),
            ]);

        Merger(g).Apply(
            artifacts,
            PlansFor(clips, ["crossfade", "cut", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Single(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(2, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(51 - 8, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_MissingLaterRunAssembler_DegradesAllOverlapsToCuts()
    {
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        ArchitectureId missingArchitecture = new("missing");
        List<DecodedClipArtifact> artifacts = Artifacts(
            clips,
            [
                Ltx2ArchitectureModule.ArchitectureId,
                Ltx2ArchitectureModule.ArchitectureId,
                missingArchitecture,
                missingArchitecture,
            ]);
        BoundaryBudgetResolution resolution = Merger(g).Apply(
            artifacts,
            PlansFor(
                clips,
                ["crossfade", "cut", "crossfade", "cut"]));

        Assert.True(resolution.Degraded);
        Assert.Contains("no boundary assembler", resolution.Reason);
        Assert.All(
            resolution.Boundaries,
            boundary => Assert.Equal(BoundaryJoinType.Cut, boundary.Effective));
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(68, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_InsertsPixelBlendPerBoundary_WithRampMaskAndAudioTrim()
    {
        // Three 17-frame clips, both interior boundaries crossfading. K clamps to 8 (the middle clip is
        // trimmed on both sides: (17-1)/2 == 8), so 2 boundaries remove 8 frames each.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        Merger(g).Apply(
            Artifacts(clips),
            PlansFor(clips, ["crossfade", "crossfade", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        const int k = 8;

        List<LTXVLaplacianPyramidBlendNode> blends = [.. bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>()];
        Assert.Equal(2, blends.Count);

        // Slices: 3 cores + (tail+head) per boundary == 3 + 4 == 7 ImageFromBatch nodes.
        Assert.Equal(7, CountOf<ImageFromBatchNode>(bridge));
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(2, CountOf<AudioConcatNode>(bridge));

        // One shared ramp mask node (built once, reused by both blends). The per-frame white->black
        // values live inside SwarmRampMaskBatch, pinned by comfy_node/.../tests/test_ramp_mask.py.
        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(k, ramp.Frames.LiteralAsInt());
        Assert.Equal(512, ramp.Width.LiteralAsInt());
        Assert.Equal(512, ramp.Height.LiteralAsInt());
        Assert.All(blends, blend =>
            Assert.Equal(ramp.Id, blend.Mask.Connection!.Node.Id));

        // Every blend's image_a/image_b come from ImageFromBatch slices (clip tail / next clip head).
        Assert.All(blends, blend =>
        {
            Assert.IsType<ImageFromBatchNode>(blend.ImageA.Connection!.Node);
            Assert.IsType<ImageFromBatchNode>(blend.ImageB.Connection!.Node);
        });

        // Trim each outgoing crossfade separately; trimming only the final track would drift later clips.
        List<TrimAudioDurationNode> trims = [.. bridge.Graph.NodesOfType<TrimAudioDurationNode>()];
        Assert.Equal(2, trims.Count);
        Assert.All(trims, t =>
        {
            Assert.Equal(0.0, t.StartIndex.LiteralAsDouble()!.Value, 6);
            Assert.Equal((17 - k) / (double)Fps, t.Duration.LiteralAsDouble()!.Value, 6);
        });
        HashSet<string> trimmedSources = [.. trims.Select(t => t.Audio.Connection!.Node.Id)];
        Assert.Equal(new HashSet<string> { AudioId(0), AudioId(1) }, trimmedSources);
        string mergedAudioNodeId = $"{((JArray)g.CurrentMedia.AttachedAudio!.Path)[0]}";
        Assert.Contains(bridge.Graph.NodesOfType<AudioConcatNode>(), c => c.Id == mergedAudioNodeId);

        Assert.Equal(51 - 2 * k, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_DimensionMismatch_ConformsAndKeepsTheCrossfade()
    {
        // The second clip finished larger. It is conformed down to the timeline minimum before the
        // overlap merge, so the crossfade survives instead of degrading to a cut.
        (WorkflowGenerator g, List<WGNodeData> clips) = BuildClips(
            [17, 17],
            T2IModelClassSorter.CompatLtxv2,
            widths: [512, 640],
            heights: [512, 640]);
        Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["crossfade", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageScaleNode conform = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Equal(VideoId(1), conform.Image.Connection!.Node.Id);
        Assert.Equal(512, conform.Width.LiteralAsInt());
        Assert.Equal(512, conform.Height.LiteralAsInt());
        Assert.Equal(1, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(512, g.CurrentMedia.Width);
        Assert.Equal(34 - 8, g.CurrentMedia.Frames);
        List<string> warnings = Assert.IsType<List<string>>(
            g.UserInput.ExtraMeta["parser_warnings"]);
        Assert.Contains(
            warnings,
            warning => warning.Contains(
                "finished at 640x640@24 and was conformed to 512x512@24",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Conform_AspectRatioMismatch_Blocks()
    {
        // Conforming width and height independently would distort, so this is reported rather than
        // silently squashed.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2, widths: [512, 640]);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["cut", "cut"])));

        Assert.Contains("aspect ratio", error.Message);
    }

    [Fact]
    public void Crossfade_TwoClip_TrimsOnlyFirstClipAudio()
    {
        // The common single-boundary case: only clip 0 crossfades out, so ONLY its audio tail is dropped
        // (K frames) and clip 1 keeps full audio — locking that a 2-clip crossfade doesn't desync A/V.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["crossfade", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        const int k = 8;

        Assert.Equal(1, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        TrimAudioDurationNode trim = Assert.Single(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal((17 - k) / (double)Fps, trim.Duration.LiteralAsDouble()!.Value, 6);
        Assert.Equal(AudioId(0), trim.Audio.Connection!.Node.Id);
        AudioConcatNode concat = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());
        Assert.Equal(trim.Id, concat.Audio1.Connection!.Node.Id);
        Assert.Equal(AudioId(1), concat.Audio2.Connection!.Node.Id);
        Assert.Equal(34 - k, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_WithRequestedOverlap_UsesPerBoundaryDissolve()
    {
        // Production path: the per-clip BoundaryOutOverlap prefs ride into the plan, so a crossfade
        // boundary dissolves over its requested 24 frames instead of the default 8.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([49, 49], T2IModelClassSorter.CompatLtxv2);
        Merger(g).Apply(
            Artifacts(clips),
            PlansFor(clips, ["crossfade", "cut"], boundaryOverlapPrefs: [24, 8]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        const int k = 24;

        Assert.Equal(1, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(k, ramp.Frames.LiteralAsInt());
        TrimAudioDurationNode trim = Assert.Single(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal((49 - k) / (double)Fps, trim.Duration.LiteralAsDouble()!.Value, 6);
        Assert.Equal(98 - k, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_FpsMismatch_ResamplesToTheTimelineMinimum()
    {
        // The faster clip is resampled to the timeline's minimum fps, which rescales both its frame
        // count and the overlap it owns — an overlap is a duration, not a frame count.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2, framesPerSecond: [Fps, 30]);
        Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["crossfade", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        SwarmVideoResampleFPSNode resample =
            Assert.Single(bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.Equal(VideoId(1), resample.ImagesInput.Connection!.Node.Id);
        Assert.Equal(30.0, resample.FpsIn.LiteralAsDouble());
        Assert.Equal(24.0, resample.FpsOut.LiteralAsDouble());
        Assert.Equal(0, CountOf<ImageScaleNode>(bridge));
        Assert.Equal(1, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        // 17 frames @30 conform to 14 @24, and the 8-frame overlap to 6.
        Assert.Equal(17 + 14 - 6, g.CurrentMedia.Frames);
        Assert.Equal(24, g.CurrentMedia.GetRawFPS());
    }

    [Fact]
    public void Conform_AllCutTimeline_ConformsEveryClipBeforeConcat()
    {
        // All-cut timelines still require geometry and frame-rate conformance before batching.
        (WorkflowGenerator g, List<WGNodeData> clips) = BuildClips(
            [40, 32, 24],
            T2IModelClassSorter.CompatLtxv2,
            widths: [1024, 768, 512],
            heights: [1024, 768, 512],
            framesPerSecond: [40, 32, 24]);
        Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["cut", "cut", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        BatchImagesNodeNode concat = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        List<INodeOutput> concatInputs = [
            .. Enumerable.Range(0, concat.Images.Count).Select(i => concat.Images[i].Connection)
        ];
        // Clip 2 is already at the target, so it reaches the concat with no conform nodes.
        Assert.Equal(VideoId(2), concatInputs[2].Node.Id);
        foreach (int index in (int[])[0, 1])
        {
            ImageScaleNode scale = Assert.IsType<ImageScaleNode>(concatInputs[index].Node);
            Assert.Equal(512, scale.Width.LiteralAsInt());
            Assert.Equal(512, scale.Height.LiteralAsInt());
            SwarmVideoResampleFPSNode resample =
                Assert.IsType<SwarmVideoResampleFPSNode>(scale.Image.Connection!.Node);
            Assert.Equal(VideoId(index), resample.ImagesInput.Connection!.Node.Id);
            Assert.Equal(24.0, resample.FpsOut.LiteralAsDouble());
        }
        Assert.Equal(512, g.CurrentMedia.Width);
        Assert.Equal(24, g.CurrentMedia.GetRawFPS());
        // Every clip is one second long, so the conformed timeline is 3 x 24 frames.
        Assert.Equal(72, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Conform_MatchingClips_AddNoConformNodes()
    {
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        Merger(g).Apply(Artifacts(clips), PlansFor(clips, ["cut", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(0, CountOf<ImageScaleNode>(bridge));
        Assert.Equal(0, CountOf<SwarmVideoResampleFPSNode>(bridge));
    }

    [Fact]
    public void Overlap_BudgetFailureDegradesOnlyItsOwnRun()
    {
        // Clips 2/3 came back shorter than planned and cannot fund their crossfade; the unrelated
        // 0->1 crossfade across the cut must survive, instead of one bad run degrading everything.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        List<DecodedClipArtifact> artifacts = Artifacts(clips);
        artifacts[2] = artifacts[2] with { Frames = 4 };
        artifacts[3] = artifacts[3] with { Frames = 4 };
        BoundaryBudgetResolution resolution = Merger(g).Apply(
            artifacts,
            PlansFor(clips, ["crossfade", "cut", "crossfade", "cut"]));

        Assert.Equal(BoundaryJoinType.Crossfade, resolution.Boundaries[0].Effective);
        Assert.Equal(BoundaryJoinType.Cut, resolution.Boundaries[2].Effective);
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(1, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
    }

    [Fact]
    public void Overlap_ConformedNeighbourAcrossACutKeepsTheCrossfade()
    {
        // Clip 2 is cut-separated and finished at a different size. Conforming it is enough; the
        // 0->1 crossfade is untouched.
        (WorkflowGenerator g, List<WGNodeData> clips) = BuildClips(
            [17, 17, 17],
            T2IModelClassSorter.CompatLtxv2,
            widths: [512, 512, 1024],
            heights: [512, 512, 1024]);
        BoundaryBudgetResolution resolution = Merger(g).Apply(
            Artifacts(clips),
            PlansFor(clips, ["crossfade", "cut", "cut"]));

        Assert.False(resolution.Degraded);
        Assert.Equal(BoundaryJoinType.Crossfade, resolution.Boundaries[0].Effective);
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(1, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        ImageScaleNode conform = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Equal(VideoId(2), conform.Image.Connection!.Node.Id);
    }

    [Fact]
    public void Crossfade_ClipTooShortForOverlap_FallsBackToCut()
    {
        // A middle clip crossfaded on both sides needs frames > 2K for a >=1-frame overlap; a 2-frame
        // middle clip forces the clamp to 0, so the whole run degrades to cut.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 2, 17], T2IModelClassSorter.CompatLtxv2);
        Merger(g).Apply(
            Artifacts(clips),
            PlansFor(clips, ["crossfade", "crossfade", "cut"]));

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(0, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(0, CountOf<TrimAudioDurationNode>(bridge));
        Assert.Equal(36, g.CurrentMedia.Frames);
    }
}
