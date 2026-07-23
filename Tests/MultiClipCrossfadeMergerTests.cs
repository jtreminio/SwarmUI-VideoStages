using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Graph-shape tests for the multi-clip merger's Stage-D boundaryOut behavior. Drives the merger directly on
/// hand-built decoded-pixel clips, asserting crossfade/cut branching at the node level without a full LTX flow.
/// </summary>
[Collection("VideoStagesTests")]
public class MultiClipCrossfadeMergerTests
{
    private const int Fps = 24;

    private static string VideoId(int i) => $"{10 + i}";
    private static string AudioId(int i) => $"{40 + i}";

    private static WorkflowGenerator NewGenerator(JObject workflow)
    {
        // Side-effect: registers VideoStages node types (so WorkflowBridge.Create deserializes them as typed
        // nodes) plus core T2I params; return value is unused.
        _ = WorkflowTestHarness.VideoStagesSteps();
        return new WorkflowGenerator
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            Workflow = workflow,
        };
    }

    /// <summary>
    /// Builds one decoded-pixel clip per <paramref name="frames"/> entry (each with attached audio) backed by stub nodes.
    /// </summary>
    private static (WorkflowGenerator Generator, List<WGNodeData> Clips) BuildClips(
        int[] frames,
        T2IModelCompatClass compat,
        int[] widths = null,
        int height = 512)
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
                Height = height,
                Frames = frames[i],
                FPS = new JValue(Fps),
                AttachedAudio = new WGNodeData(new JArray(AudioId(i), 0), g, WGNodeData.DT_AUDIO, compat),
            });
        }
        return (g, clips);
    }

    private static int CountOf<T>(WorkflowBridge bridge) where T : ComfyNode =>
        bridge.Graph.NodesOfType<T>().Count;

    [Fact]
    public void Cut_MultiClip_ProducesExactlyTodaysGraphShape_NoCrossfadeNodes()
    {
        // Two independent runs on identical inputs: one with no boundaryOuts (the pre-Stage-D signature),
        // one with an explicit all-"cut" list. The resulting workflows must be byte-for-byte identical —
        // the regression lock that "cut" (and the new field) never perturbs today's graph.
        (WorkflowGenerator gA, List<WGNodeData> clipsA) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        new MultiClipParallelMerger(gA).Apply(clipsA, null);

        (WorkflowGenerator gB, List<WGNodeData> clipsB) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        new MultiClipParallelMerger(gB).Apply(clipsB, ["cut", "cut", "cut"]);

        Assert.True(JToken.DeepEquals(gA.Workflow, gB.Workflow),
            "All-cut boundaryOuts must produce the identical graph to the pre-Stage-D signature.");

        using WorkflowBridge bridge = WorkflowBridge.Create(gB.Workflow);
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(2, CountOf<AudioConcatNode>(bridge));
        Assert.Equal(0, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(0, CountOf<ImageFromBatchNode>(bridge));
        Assert.Equal(0, CountOf<SwarmRampMaskBatchNode>(bridge));
        Assert.Equal(0, CountOf<TrimAudioDurationNode>(bridge));
        // Full length preserved (no overlap removed).
        Assert.Equal(51, gB.CurrentMedia.Frames);
    }

    [Fact]
    public void Continue_WithoutWindows_DefaultsToOneFrameBlendAndAudioTrim()
    {
        // No continueWindows supplied: an armed "continue" boundary falls back to the conservative
        // 1-frame window — a 50/50 blend of the two duplicated seam frames — and trims clip 0's audio
        // by that frame to stay in sync.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        new MultiClipParallelMerger(g).Apply(clips, ["continue", "cut"]);

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

        // One duplicated seam frame removed.
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
        int[] windows = MultiClipParallelMerger.ResolveContinueWindows(
            [17, 17], ["continue", "cut"], [8, 8]);
        Assert.Equal([9], windows);
        new MultiClipParallelMerger(g).Apply(clips, ["continue", "cut"], windows);

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
    public void ContinueAndCrossfade_MixedBoundaries_UsePerBoundaryOverlaps()
    {
        // Boundary 0 is an armed "continue" (window 9), boundary 1 a crossfade whose K clamps to 7: the
        // middle clip spends 9 frames on the continue side, leaving 17-1-9=7. Each boundary gets its own
        // ramp mask.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        int[] windows = MultiClipParallelMerger.ResolveContinueWindows(
            [17, 17, 17], ["continue", "crossfade", "cut"], [8, 8, 8]);
        Assert.Equal([9, 0], windows);
        new MultiClipParallelMerger(g).Apply(clips, ["continue", "crossfade", "cut"], windows);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        const int contK = 9;
        const int crossK = 7;

        // One blend per boundary; 3 cores + 2x(tail+head) slices.
        Assert.Equal(2, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(7, CountOf<ImageFromBatchNode>(bridge));

        // Two distinct ramp masks: the K=9 continue ramp plus the K=7 crossfade ramp.
        List<SwarmRampMaskBatchNode> ramps = [.. bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>()];
        Assert.Equal(2, ramps.Count);
        Assert.Contains(ramps, r => r.Frames.LiteralAsInt() == contK);
        Assert.Contains(ramps, r => r.Frames.LiteralAsInt() == crossK);

        // Audio: clip 0 sheds its 9-frame continue tail, clip 1 its 7-frame crossfade tail.
        List<TrimAudioDurationNode> trims = [.. bridge.Graph.NodesOfType<TrimAudioDurationNode>()];
        Assert.Equal(2, trims.Count);
        Dictionary<string, double> trimBySource = trims.ToDictionary(
            t => t.Audio.Connection!.Node.Id,
            t => t.Duration.LiteralAsDouble()!.Value);
        Assert.Equal((17 - contK) / (double)Fps, trimBySource[AudioId(0)], 6);
        Assert.Equal((17 - crossK) / (double)Fps, trimBySource[AudioId(1)], 6);

        // 51 total minus 9 (continue) minus 7 (crossfade).
        Assert.Equal(51 - contK - crossK, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_InsertsPixelBlendPerBoundary_WithRampMaskAndAudioTrim()
    {
        // Three 17-frame clips, both interior boundaries crossfading. K clamps to 8 (the middle clip is
        // trimmed on both sides: (17-1)/2 == 8), so 2 boundaries remove 8 frames each.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17, 17], T2IModelClassSorter.CompatLtxv2);
        new MultiClipParallelMerger(g).Apply(clips, ["crossfade", "crossfade", "cut"]);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        const int k = 8;

        // One pixel-blend per crossfaded boundary.
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

        // Audio aligns PER crossfaded boundary: each clip whose outgoing boundary crossfades (clip 0, 1)
        // drops its tail K frames before the concat, keeping later clips' audio synced with their K-earlier
        // video; clip 2 (cut) keeps full audio. A single whole-track trim matches total length but drifts
        // +K per interior boundary — the regression this locks.
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

        // Merged frame count shrinks by the removed overlap.
        Assert.Equal(51 - 2 * k, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_DimensionMismatch_FallsBackToCut()
    {
        // Second clip has a different width, so the crossfade preconditions fail and everything degrades
        // to the unchanged cut concat.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2, widths: [512, 640]);
        new MultiClipParallelMerger(g).Apply(clips, ["crossfade", "cut"]);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(0, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(0, CountOf<ImageFromBatchNode>(bridge));
        Assert.Equal(0, CountOf<TrimAudioDurationNode>(bridge));
        Assert.Equal(34, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_TwoClip_TrimsOnlyFirstClipAudio()
    {
        // The common single-boundary case: only clip 0 crossfades out, so ONLY its audio tail is dropped
        // (K frames) and clip 1 keeps full audio — locking that a 2-clip crossfade doesn't desync A/V.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        new MultiClipParallelMerger(g).Apply(clips, ["crossfade", "cut"]);

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
        new MultiClipParallelMerger(g).Apply(clips, ["crossfade", "cut"], null, [24, 8]);

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
    public void Crossfade_FpsMismatch_FallsBackToCut()
    {
        // fps must match across clips (the audio trim converts frames->seconds); a mismatch degrades to cut.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        clips[1].FPS = new JValue(30);
        new MultiClipParallelMerger(g).Apply(clips, ["crossfade", "cut"]);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(0, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(0, CountOf<TrimAudioDurationNode>(bridge));
        Assert.Equal(34, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_UnknownFrameCount_FallsBackToCut()
    {
        // If any clip's frame count is unknown, the overlap math is undefined, so crossfade degrades to cut.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 17], T2IModelClassSorter.CompatLtxv2);
        clips[1].Frames = null;
        new MultiClipParallelMerger(g).Apply(clips, ["crossfade", "cut"]);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(0, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(0, CountOf<TrimAudioDurationNode>(bridge));
    }

    [Fact]
    public void Crossfade_ClipTooShortForOverlap_FallsBackToCut()
    {
        // A middle clip crossfaded on both sides needs frames > 2K for a >=1-frame overlap; a 2-frame
        // middle clip forces the clamp to 0, so the whole run degrades to cut.
        (WorkflowGenerator g, List<WGNodeData> clips) =
            BuildClips([17, 2, 17], T2IModelClassSorter.CompatLtxv2);
        new MultiClipParallelMerger(g).Apply(clips, ["crossfade", "crossfade", "cut"]);

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        Assert.Equal(1, CountOf<BatchImagesNodeNode>(bridge));
        Assert.Equal(0, CountOf<LTXVLaplacianPyramidBlendNode>(bridge));
        Assert.Equal(0, CountOf<TrimAudioDurationNode>(bridge));
        Assert.Equal(36, g.CurrentMedia.Frames);
    }
}
