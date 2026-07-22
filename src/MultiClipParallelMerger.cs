using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Generated;

namespace VideoStages;

internal sealed class MultiClipParallelMerger(WorkflowGenerator g)
{
    internal const string NodeHelperKey = "videostages.parallel-multi-clip";
    private const int BatchImagesNodeMaxInputs = 50;
    // Crossfade dissolve length when no per-clip BoundaryOutOverlap preference is supplied; each
    // boundary's request is clamped so every crossfaded clip keeps >=1 non-overlapped core frame
    // (see ResolveCrossfadePlan).
    private const int DefaultCrossfadeOverlapFrames = 8;

    // Per-boundary overlap window in frames (0 = hard cut at that boundary). A crossfade boundary's
    // window is its requested dissolve length clamped to both clips' budgets; a "continue" boundary's
    // window is its resolved overlap+1 — the frames duplicated by generation-time continuity conditioning.
    internal sealed record CrossfadePlan(int[] BoundaryOverlap, int RemovedFrames);

    public void Apply(
        IReadOnlyList<WGNodeData> clipOutputsInOrder,
        WGNodeData parallelClipSourceMedia = null,
        IReadOnlyList<string> clipBoundaryOuts = null,
        IReadOnlyList<int> continueWindows = null,
        IReadOnlyList<int> boundaryOverlapPrefs = null)
    {
        if (clipOutputsInOrder is null || clipOutputsInOrder.Count < 2)
        {
            return;
        }

        List<WGNodeData> resolvedAudio = [];
        int sumFrames = 0;
        bool allFramesKnown = true;
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            WGNodeData audio = TryGetClipConcatenatableAudio(clip);
            if (audio is not null)
            {
                resolvedAudio.Add(audio);
            }

            if (allFramesKnown)
            {
                if (clip?.Frames is int f)
                {
                    sumFrames += f;
                }
                else
                {
                    allFramesKnown = false;
                }
            }
        }

        using WorkflowBridge bridge = BridgeSync.For(g);

        List<INodeOutput> videoOutputs = [];
        HashSet<string> terminalKeys = [];
        foreach (WGNodeData clip in clipOutputsInOrder)
        {
            INodeOutput output = bridge.ResolvePath(clip?.Path);
            if (output is null)
            {
                continue;
            }
            videoOutputs.Add(output);
            terminalKeys.Add(OutputKey(output));
        }

        if (videoOutputs.Count < 2)
        {
            return;
        }

        List<INodeOutput> audioOutputs = [];
        foreach (WGNodeData audio in resolvedAudio)
        {
            INodeOutput output = bridge.ResolvePath(audio.Path);
            if (output is not null)
            {
                audioOutputs.Add(output);
            }
        }

        // Null plan => the pure-cut path below runs byte-for-byte as before: a regression guarantee for
        // all-cut, non-LTX, dimension/fps-mismatch, and unknown-frame configs. ("continue" reaches this
        // method only when generation-time continuity was actually armed; unarmed ones arrive as "cut".)
        CrossfadePlan crossfadePlan = ResolveCrossfadePlan(
            clipOutputsInOrder, clipBoundaryOuts, allFramesKnown, continueWindows, boundaryOverlapPrefs);
        // Crossfade indexes clips[i]/CrossfadeBoundary[i] against videoOutputs[i]/audioOutputs[i]; a failed
        // clip resolution shortens videoOutputs and desyncs the lists (wrong frames/audio). Require 1:1
        // here; the cut path tolerates a resolved subset.
        if (crossfadePlan is not null && videoOutputs.Count != clipOutputsInOrder.Count)
        {
            Logs.Warning(
                "VideoStages: crossfade requested but not every clip output resolved; "
                + "falling back to a hard cut.");
            crossfadePlan = null;
        }

        int removedFrames = 0;
        INodeOutput mergedVideo;
        if (crossfadePlan is null)
        {
            mergedVideo = MergeClipVideosWithBatchImagesNode(bridge, videoOutputs);
        }
        else
        {
            mergedVideo = MergeClipVideosWithCrossfade(bridge, clipOutputsInOrder, videoOutputs, crossfadePlan);
            removedFrames = crossfadePlan.RemovedFrames;
        }

        if (audioOutputs.Count > 0 && audioOutputs.Count != videoOutputs.Count)
        {
            Logs.Warning(
                $"VideoStages: merged clip audio omitted — only {audioOutputs.Count} of "
                + $"{videoOutputs.Count} clips have concatenatable audio.");
        }
        INodeOutput mergedAudio = audioOutputs.Count == videoOutputs.Count && audioOutputs.Count > 0
            ? BuildMergedAudio(bridge, clipOutputsInOrder, audioOutputs, crossfadePlan)
            : null;

        INodeOutput rootVideoOutput = bridge.ResolvePath(parallelClipSourceMedia?.Path);
        if (rootVideoOutput is not null)
        {
            terminalKeys.Add(OutputKey(rootVideoOutput));
        }

        RetargetSwarmSaveAnimationWsForClipTerminals(bridge, terminalKeys, mergedVideo, mergedAudio);

        WGNodeData template = clipOutputsInOrder[0];
        g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(mergedVideo), g, WGNodeData.DT_VIDEO, template.Compat)
        {
            Width = template.Width,
            Height = template.Height,
            Frames = allFramesKnown ? sumFrames - removedFrames : template.Frames,
            FPS = template.FPS
        };
        if (mergedAudio is not null)
        {
            g.CurrentMedia.AttachedAudio = new WGNodeData(
                WorkflowBridge.ToPath(mergedAudio),
                g,
                WGNodeData.DT_AUDIO,
                template.AttachedAudio?.Compat ?? g.CurrentAudioVae?.Compat);
        }
    }

    private static string OutputKey(INodeOutput output) => $"{output.Node.Id}::{output.SlotIndex}";

    private WGNodeData TryGetClipConcatenatableAudio(WGNodeData clip)
    {
        WGNodeData attached = clip?.AttachedAudio;
        if (attached?.Path is not JArray { Count: 2 })
        {
            return null;
        }

        if (attached.DataType == WGNodeData.DT_AUDIO)
        {
            return attached;
        }

        if (attached.DataType == WGNodeData.DT_LATENT_AUDIO && g.CurrentAudioVae is not null)
        {
            WGNodeData decoded = attached.DecodeLatents(g.CurrentAudioVae, true);
            if (decoded?.Path is JArray { Count: 2 } && decoded.DataType == WGNodeData.DT_AUDIO)
            {
                return decoded;
            }
        }

        return null;
    }

    private static INodeOutput MergeClipVideosWithBatchImagesNode(
        WorkflowBridge bridge,
        IReadOnlyList<INodeOutput> outputs)
    {
        if (outputs.Count == 1)
        {
            return outputs[0];
        }

        List<INodeOutput> layer = [.. outputs];
        while (layer.Count > BatchImagesNodeMaxInputs)
        {
            INodeOutput chunk = AddBatchImagesNode(bridge, layer.Take(BatchImagesNodeMaxInputs));
            List<INodeOutput> next = [chunk];
            for (int i = BatchImagesNodeMaxInputs; i < layer.Count; i++)
            {
                next.Add(layer[i]);
            }
            layer = next;
        }

        return AddBatchImagesNode(bridge, layer);
    }

    private static INodeOutput AddBatchImagesNode(WorkflowBridge bridge, IEnumerable<INodeOutput> imageOutputs)
    {
        BatchImagesNodeNode node = bridge.AddNode(new BatchImagesNodeNode());
        foreach (INodeOutput imageOutput in imageOutputs)
        {
            node.Images.AddFromUntyped(imageOutput);
        }

        return node.IMAGE;
    }

    /// <summary>
    /// Concatenates per-clip audio aligned to the overlapped video. Plain cut: straight concat. An
    /// overlapping boundary (crossfade K, or continue's window) drops that clip's tail K frames before
    /// the concat (that tail is consumed by the video blend), so later audio starts where its K-earlier
    /// video does; a whole-track trim would instead drift +K per interior boundary. Seams are hard cuts,
    /// not audio cross-dissolves (overlap-add deferred). Plan non-null implies fps &gt; 0, known frame
    /// counts, and 1:1 clip/output alignment (ResolveCrossfadePlan), so the unguarded .Value derefs below
    /// are safe.
    /// </summary>
    private INodeOutput BuildMergedAudio(
        WorkflowBridge bridge,
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<INodeOutput> audioOutputs,
        CrossfadePlan plan)
    {
        if (plan is null)
        {
            return CascadeAudioConcat(bridge, audioOutputs);
        }

        int fps = clips[0].GetRawFPS().Value;
        List<INodeOutput> aligned = [];
        for (int i = 0; i < audioOutputs.Count; i++)
        {
            int rightOverlap = i < audioOutputs.Count - 1 ? plan.BoundaryOverlap[i] : 0;
            if (rightOverlap > 0)
            {
                double keptSeconds = Math.Max(0, clips[i].Frames.Value - rightOverlap) / (double)fps;
                aligned.Add(TrimAudioToDuration(bridge, audioOutputs[i], keptSeconds));
            }
            else
            {
                aligned.Add(audioOutputs[i]);
            }
        }
        return CascadeAudioConcat(bridge, aligned);
    }

    private static INodeOutput CascadeAudioConcat(WorkflowBridge bridge, IReadOnlyList<INodeOutput> audioOutputs)
    {
        INodeOutput acc = audioOutputs[0];
        for (int i = 1; i < audioOutputs.Count; i++)
        {
            AudioConcatNode concat = bridge.AddNode(new AudioConcatNode());
            concat.Audio1.ConnectToUntyped(acc);
            concat.Audio2.ConnectToUntyped(audioOutputs[i]);
            bridge.SyncNode(concat);
            acc = concat.AUDIO;
        }

        return acc;
    }

    /// <summary>
    /// Resolves each "continue" boundary's overlap window: the requested overlap + 1 (an 8n+1 frame
    /// count matching the LTX causal VAE grid), stepped down (… 17, 9, 1) until both adjacent clips
    /// keep &gt;=1 core frame — reserving one frame on each neighbour that funds another overlapping
    /// boundary of its own. Unknown frame counts degrade that boundary to the conservative 1-frame
    /// window. Non-continue boundaries resolve to 0. StageSequenceRunner slices conditioning tails
    /// with these windows and passes the same array to <see cref="Apply"/> so generation and merge
    /// agree by construction.
    /// </summary>
    // internal (not private): half of the cross-language M1 drift pair with frontend
    // boundaryPlan.resolveContinueWindows.
    internal static int[] ResolveContinueWindows(
        IReadOnlyList<int?> frames,
        IReadOnlyList<string> boundaryOuts,
        IReadOnlyList<int> requestedOverlaps)
    {
        int boundaryCount = Math.Max(0, frames.Count - 1);
        bool[] cont = new bool[boundaryCount];
        bool[] crossfade = new bool[boundaryCount];
        for (int i = 0; i < boundaryCount; i++)
        {
            string boundary = boundaryOuts is not null && i < boundaryOuts.Count
                ? boundaryOuts[i]
                : Constants.BoundaryOutCut;
            cont[i] = string.Equals(boundary, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase);
            crossfade[i] = string.Equals(boundary, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase);
        }

        int[] windows = new int[boundaryCount];
        for (int i = 0; i < boundaryCount; i++)
        {
            if (!cont[i])
            {
                continue;
            }
            if (frames[i] is not int left || frames[i + 1] is not int right)
            {
                windows[i] = 1;
                continue;
            }
            int requested = requestedOverlaps is not null && i < requestedOverlaps.Count
                ? requestedOverlaps[i]
                : Constants.ContinueOverlapDefaultFrames;
            int k = requested + 1;
            int leftReserve = i > 0 && cont[i - 1] ? windows[i - 1] : i > 0 && crossfade[i - 1] ? 1 : 0;
            int rightReserve = i < boundaryCount - 1 && (cont[i + 1] || crossfade[i + 1]) ? 1 : 0;
            int kMax = Math.Min(left - 1 - leftReserve, right - 1 - rightReserve);
            while (k > 1 && k > kMax)
            {
                k -= 8;
            }
            windows[i] = Math.Max(1, k);
        }
        return windows;
    }

    /// <summary>
    /// Decides the per-boundary overlap windows, or null to fall back to the pure-cut merge. Gated on:
    /// &gt;=1 boundary requesting an overlap ("crossfade" or "continue"), LTXV2 pixel output on every
    /// clip, shared width/height/fps, and known frame counts. A crossfade boundary takes its requested
    /// dissolve length from <paramref name="boundaryOverlapPrefs"/> (the left clip's BoundaryOutOverlap;
    /// absent list = 8), clamped so each adjacent clip keeps &gt;=1 core frame after its continue trims —
    /// a clip funding two crossfades splits its budget evenly between them. A "continue" boundary takes
    /// its entry from <paramref name="continueWindows"/> (its resolved overlap+1; absent list = 1) —
    /// generation-time continuity conditions the next clip's first window frames on the previous clip's
    /// last, so the duplicated frames collapse. Callers must only pass "continue" for boundaries where
    /// that conditioning was actually applied (StageSequenceRunner degrades unarmed ones to "cut").
    /// </summary>
    // internal (not private): the cross-language crossfade-plan drift test (M1) pins this against the
    // frontend boundaryPlan.crossfadePlanForClips mirror.
    internal static CrossfadePlan ResolveCrossfadePlan(
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<string> clipBoundaryOuts,
        bool allFramesKnown,
        IReadOnlyList<int> continueWindows = null,
        IReadOnlyList<int> boundaryOverlapPrefs = null)
    {
        int count = clips.Count;
        bool[] crossfadeBoundary = new bool[count - 1];
        bool[] continueBoundary = new bool[count - 1];
        int requested = 0;
        for (int i = 0; i < count - 1; i++)
        {
            string boundary = clipBoundaryOuts is not null && i < clipBoundaryOuts.Count
                ? clipBoundaryOuts[i]
                : Constants.BoundaryOutCut;
            if (string.Equals(boundary, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase))
            {
                crossfadeBoundary[i] = true;
                requested++;
            }
            else if (string.Equals(boundary, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase))
            {
                continueBoundary[i] = true;
                requested++;
            }
        }
        if (requested == 0)
        {
            return null;
        }

        WGNodeData first = clips[0];
        foreach (WGNodeData clip in clips)
        {
            bool uniform = clip.Width is int w && w > 0 && w == first.Width
                && clip.Height is int h && h > 0 && h == first.Height
                && SameFps(clip, first);
            if (!allFramesKnown || clip.Frames is not int f || f <= 0
                || !uniform
                || !VideoStageModelCompat.IsLtxV2VideoModel(clip.Compat))
            {
                Logs.Warning(
                    "VideoStages: crossfade/continue boundary requested but unavailable "
                    + "(needs LTXV2 clips with matching width/height/fps and known frame counts); "
                    + "falling back to a hard cut.");
                return null;
            }
        }

        // Per-clip crossfade budget: frames minus 1 core frame minus the window trims that adjacent
        // "continue" boundaries impose, split evenly between the clip's crossfaded sides. If a clip
        // can't spare its continue trims (budget < 0) or a crossfaded clip can't spare a single
        // overlap frame, fall back.
        int ContinueWindow(int i) => continueWindows is not null && i < continueWindows.Count
            ? Math.Max(1, continueWindows[i])
            : 1;
        int CrossfadeRequested(int i) => boundaryOverlapPrefs is not null && i < boundaryOverlapPrefs.Count
            ? Math.Max(1, boundaryOverlapPrefs[i])
            : DefaultCrossfadeOverlapFrames;
        int[] crossfadeMaxPerSide = new int[count];
        for (int i = 0; i < count; i++)
        {
            int fixedTrim = (i > 0 && continueBoundary[i - 1] ? ContinueWindow(i - 1) : 0)
                + (i < count - 1 && continueBoundary[i] ? ContinueWindow(i) : 0);
            int crossSides = (i > 0 && crossfadeBoundary[i - 1] ? 1 : 0)
                + (i < count - 1 && crossfadeBoundary[i] ? 1 : 0);
            if (fixedTrim == 0 && crossSides == 0)
            {
                continue;
            }
            int budget = clips[i].Frames.Value - 1 - fixedTrim;
            if (budget < 0 || (crossSides > 0 && budget / crossSides < 1))
            {
                Logs.Warning(
                    "VideoStages: crossfade/continue boundary requested but a clip is too short for any "
                    + "overlap window; falling back to a hard cut.");
                return null;
            }
            if (crossSides > 0)
            {
                crossfadeMaxPerSide[i] = budget / crossSides;
            }
        }

        int[] boundaryOverlap = new int[count - 1];
        int removedFrames = 0;
        for (int i = 0; i < count - 1; i++)
        {
            boundaryOverlap[i] = crossfadeBoundary[i]
                ? Math.Min(CrossfadeRequested(i), Math.Min(crossfadeMaxPerSide[i], crossfadeMaxPerSide[i + 1]))
                : continueBoundary[i] ? ContinueWindow(i) : 0;
            removedFrames += boundaryOverlap[i];
        }
        return new CrossfadePlan(boundaryOverlap, removedFrames);
    }

    // Positive, shared fps. The >0 guard matters: crossfade converts frames->seconds for the audio trim,
    // so a 0/negative fps (even if equal across clips) must not admit the plan.
    private static bool SameFps(WGNodeData a, WGNodeData b)
    {
        int? fa = a.GetRawFPS();
        int? fb = b.GetRawFPS();
        return fa is int va && va > 0 && fb is int vb && vb > 0 && va == vb;
    }

    /// <summary>
    /// Builds the overlapped video: each clip emits its non-overlapped core, and after every overlapping
    /// boundary a per-frame pixel dissolve of clip N's tail K frames with clip N+1's head K frames. Per
    /// pair: N[:-K] ++ blend(N.tail K, N+1.head K) ++ N+1[K:]; K is the boundary's overlap (the clamped
    /// requested dissolve for crossfades, the resolved overlap+1 window for continue). All segments feed
    /// the same BatchImages concat as the cut path (so &gt;50 segments still chunk).
    /// </summary>
    private INodeOutput MergeClipVideosWithCrossfade(
        WorkflowBridge bridge,
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<INodeOutput> videoOutputs,
        CrossfadePlan plan)
    {
        // Ramp masks are built lazily per distinct window size and reused across boundaries. White (1.0)
        // selects clip N, black (0.0) selects clip N+1; the white->black ramp dissolves N into N+1 over K.
        // A continue boundary's frames are near-duplicates, so its ramp just smooths VAE re-encode drift.
        Dictionary<int, INodeOutput> rampMasks = [];
        INodeOutput RampMaskFor(int k)
        {
            if (!rampMasks.TryGetValue(k, out INodeOutput mask))
            {
                mask = BuildCrossfadeRampMask(bridge, k, clips[0].Width.Value, clips[0].Height.Value);
                rampMasks[k] = mask;
            }
            return mask;
        }

        List<INodeOutput> segments = [];
        int count = videoOutputs.Count;
        for (int i = 0; i < count; i++)
        {
            int startTrim = i > 0 ? plan.BoundaryOverlap[i - 1] : 0;
            int endTrim = i < count - 1 ? plan.BoundaryOverlap[i] : 0;
            int frames = clips[i].Frames.Value;
            int coreLength = frames - startTrim - endTrim;
            segments.Add(SliceImageFrames(bridge, videoOutputs[i], startTrim, coreLength));

            if (endTrim > 0)
            {
                INodeOutput tail = SliceImageFrames(bridge, videoOutputs[i], frames - endTrim, endTrim);
                INodeOutput head = SliceImageFrames(bridge, videoOutputs[i + 1], 0, endTrim);
                segments.Add(AddPyramidBlend(bridge, tail, head, RampMaskFor(endTrim)));
            }
        }

        return MergeClipVideosWithBatchImagesNode(bridge, segments);
    }

    private static INodeOutput SliceImageFrames(
        WorkflowBridge bridge,
        INodeOutput source,
        int batchIndex,
        int length)
    {
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode()).With(
            BatchIndex: batchIndex,
            Length: length);
        node.Image.ConnectToUntyped(source);
        return node.IMAGE;
    }

    private static INodeOutput BuildCrossfadeRampMask(WorkflowBridge bridge, int k, int width, int height)
    {
        // One node instead of K SolidMasks + a BatchMasks fan-in; the ramp values (1.0 -> 0.0 linear,
        // lone frame 0.5) live in the node (comfy_node/SwarmVideoStagesNodes/ramp_mask.py).
        SwarmRampMaskBatchNode ramp = bridge.AddNode(new SwarmRampMaskBatchNode().With(
            Frames: k,
            Width: width,
            Height: height));
        return ramp.Mask;
    }

    private static INodeOutput AddPyramidBlend(
        WorkflowBridge bridge,
        INodeOutput imageA,
        INodeOutput imageB,
        INodeOutput mask)
    {
        // LTXVLaplacianPyramidBlend (pyramid_blending.py): per-frame MASK, white=image_a, black=image_b.
        // With a spatially uniform mask the Laplacian blend reduces to a plain linear cross-dissolve at
        // that alpha — exactly the intended ramp. mask_low_res_dilation=0 skips the dilate/resize round-trip
        // (moot on a flat mask); trim_to_shortest stays true as a length guard (tail K, head K, mask K match).
        LTXVLaplacianPyramidBlendNode blend = bridge.AddNode(new LTXVLaplacianPyramidBlendNode().With(
            TrimToShortest: true,
            MaskLowResDilation: 0));
        blend.ImageA.ConnectToUntyped(imageA);
        blend.ImageB.ConnectToUntyped(imageB);
        blend.Mask.ConnectToUntyped(mask);
        return blend.Image;
    }

    private static INodeOutput TrimAudioToDuration(WorkflowBridge bridge, INodeOutput audio, double durationSeconds)
    {
        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: 0.0,
            Duration: durationSeconds);
        trim.Audio.ConnectToUntyped(audio);
        return trim.AUDIO;
    }

    private static void RetargetSwarmSaveAnimationWsForClipTerminals(
        WorkflowBridge bridge,
        HashSet<string> terminalKeys,
        INodeOutput images,
        INodeOutput audio)
    {
        if (images is null || terminalKeys.Count == 0)
        {
            return;
        }

        SaveAnimationRetargeter.Retarget(
            bridge,
            save => save.Images.Connection is INodeOutput existingImages
                && terminalKeys.Contains(OutputKey(existingImages)),
            images,
            audio,
            retargetAudio: true);
    }
}
