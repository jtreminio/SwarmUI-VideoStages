using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Clip-to-clip boundaries on LTX-2 — the continuity tail, its per-stage re-anchoring, the seam
/// blend and the audio carry — generated through the real Comfy API POST path.
/// <para>
/// Frame math, pinned once here: a 0.6s clip at 24 fps is 16 structural frames, which LTX's 8k+1
/// grid snaps up to 17. A continue target additionally generates an 8-frame hidden handle in front
/// of its own 17, so it samples 25 and the two clips share a 9-frame overlap.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class Ltx2BoundaryContractTests
{
    /// <summary>Frames a 0.6-second clip generates.</summary>
    private const int ClipFrames = 17;

    /// <summary>Frames a 0.6-second continue target generates: 17 authored plus the 8-frame handle.</summary>
    private const int TargetFrames = 25;

    /// <summary>Hidden lead-in a continue target generates in front of its authored frames.</summary>
    private const int ContinueHandle = 8;

    /// <summary>Handle plus the one shared frame the continuity anchor freezes.</summary>
    private const int ContinueOverlap = 9;

    /// <summary>A crossfade overlaps by the bare handle — it has no frozen anchor frame.</summary>
    private const int CrossfadeOverlap = 8;

    /// <summary>First frame of the continuity tail: <see cref="ClipFrames"/> - <see cref="ContinueOverlap"/>.</summary>
    private const int TailIndex = 8;

    /// <summary>Frames a 5.0-second clip generates.</summary>
    private const int LongClipFrames = 121;

    private static JObject ContinueClip(double duration, params JObject[] stages)
    {
        JObject clip = MakeClip(duration, stages);
        clip["boundaryOut"] = Constants.BoundaryOutContinue;
        return clip;
    }

    /// <summary>The decode of a clip's final stage — the pixels the timeline merge slices up.</summary>
    private static string ClipOutputDecodeId(WorkflowBridge bridge, SwarmKSamplerNode sampler)
    {
        LTXVSeparateAVLatentNode separate = OutputOf(bridge, sampler);
        return Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeTiledNode>(),
            node => node.Samples.Connection?.Node.Id == separate.Id
                && bridge.Graph.FindInputsConnectedTo(node.IMAGE).Any(
                    consumer => consumer.Node is ImageFromBatchNode)).Id;
    }

    /// <summary>
    /// The continuity tail slice. The merge cuts an identical index/length slice off the same
    /// decode, so only the consumer tells them apart: the continuity tail feeds a guide chain.
    /// </summary>
    private static ImageFromBatchNode ContinuityTailSlice(WorkflowBridge bridge, string sourceNodeId) =>
        Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.BatchIndex.LiteralAsInt() == TailIndex
                && node.Length.LiteralAsInt() == ContinueOverlap
                && node.Image.Connection?.Node.Id == sourceNodeId
                && bridge.Graph.FindInputsConnectedTo(node.IMAGE).Any(
                    consumer => consumer.Node is ImageScaleNode or LTXVPreprocessNode));

    /// <summary>
    /// Image scales between an in-place anchor and the continuity tail, or null when the anchor
    /// does not trace back to that tail at all.
    /// </summary>
    private static List<(int Width, int Height)> TailGuideScales(
        LTXVImgToVideoInplaceNode inplace,
        string tailNodeId)
    {
        List<(int Width, int Height)> scales = [];
        INodeOutput current = inplace.Image.Connection;
        while (current is not null && current.Node.Id != tailNodeId)
        {
            switch (current.Node)
            {
                case ImageScaleNode scale:
                    scales.Add((scale.Width.LiteralAsInt()!.Value, scale.Height.LiteralAsInt()!.Value));
                    current = scale.Image.Connection;
                    break;
                case LTXVPreprocessNode preprocess:
                    current = preprocess.Image.Connection;
                    break;
                default:
                    return null;
            }
        }
        return current is null ? null : scales;
    }

    private static List<LTXVImgToVideoInplaceNode> TailAnchors(
        WorkflowBridge bridge,
        string tailNodeId) =>
        [.. bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()
            .Where(node => TailGuideScales(node, tailNodeId) is not null)
            .OrderBy(node => int.Parse(node.Id))];

    /// <summary>Ordered geometry of every slice the timeline merge consumes.</summary>
    private static List<(int Index, int Length)> MergeSlices(WorkflowBridge bridge) =>
        [.. bridge.Graph.NodesOfType<ImageFromBatchNode>()
            .Where(node => bridge.Graph.FindInputsConnectedTo(node.IMAGE).Any(
                consumer => consumer.Node is ImageCompositeMaskedNode or BatchImagesNodeNode))
            .Select(node => (node.BatchIndex.LiteralAsInt()!.Value, node.Length.LiteralAsInt()!.Value))
            .OrderBy(slice => slice.Item1)
            .ThenBy(slice => slice.Item2)];

    /// <summary>
    /// The published geometry of a two-clip continue: clip 0 contributes 8 clean frames plus the
    /// 9-frame blend, clip 1 the same 9 blended frames plus its remaining 16 — 33 in all.
    /// </summary>
    private static readonly List<(int Index, int Length)> ContinueMergeSlices =
        [(0, 8), (0, 9), (8, 9), (9, 16)];

    [Fact]
    public async Task Continue_boundary_seeds_the_next_clip_from_the_previous_tail_and_collapses_the_seam()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = ContinueClip(0.6, fixture.Stage(control: 0.5, steps: 10));
        JObject second = MakeClip(0.6, fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstClip = StageSampler(bridge, 0);
        SwarmKSamplerNode secondClip = StageSampler(bridge, 1);

        ImageFromBatchNode tail = ContinuityTailSlice(bridge, ClipOutputDecodeId(bridge, firstClip));
        LTXVImgToVideoInplaceNode anchor = Assert.Single(TailAnchors(bridge, tail.Id));
        // Full strength: the tail is frozen context, not a hint.
        Assert.Equal(1.0, anchor.Strength.LiteralAsDouble());
        Assert.Same(anchor, JointLatentOf(secondClip).VideoLatent.Connection?.Node);
        Assert.Equal(
            TargetFrames,
            Assert.IsType<EmptyLTXVLatentVideoNode>(anchor.LatentInput.Connection?.Node)
                .Length.LiteralAsInt());

        // The merge collapses the duplicated overlap: one blend over one 9-frame ramp mask.
        ImageCompositeMaskedNode blend = Assert.Single(
            bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(ContinueOverlap, ramp.Frames.LiteralAsInt());
        Assert.Same(ramp.Mask, blend.Mask.Connection);
        Assert.Equal(ContinueMergeSlices, MergeSlices(bridge));

        // Clip 0's audio runs to the end of the blend and clip 1's picks up after it, dropping its
        // 8-frame handle: 16 + 17 = the 33 frames the slices publish.
        AudioConcatNode audio = Assert.IsType<AudioConcatNode>(live.PublishedAudio());
        TrimAudioDurationNode head = Assert.IsType<TrimAudioDurationNode>(
            audio.Audio1.Connection?.Node);
        TrimAudioDurationNode rest = Assert.IsType<TrimAudioDurationNode>(
            audio.Audio2.Connection?.Node);
        Assert.Equal(0.0, head.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        // ClipFrames - 1: the seam's duplicated authored frame belongs to clip 1.
        Assert.Equal((ClipFrames - 1) / 24.0, head.Duration.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(ContinueHandle / 24.0, rest.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(ClipFrames / 24.0, rest.Duration.LiteralAsDouble()!.Value, precision: 6);

        live.AssertAllLive(firstClip, secondClip, tail, anchor, blend, ramp, audio);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The continue target generates an 8-frame handle in front of its authored frames, but an
    /// authored reference is placed against the authored geometry: a frame-2 start reference lands
    /// at 10 (2 + the handle) and a frame-2 end reference stays at -2.
    /// </summary>
    [Fact]
    public async Task Continue_boundary_shifts_start_refs_but_keeps_end_refs_relative_to_the_end()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = ContinueClip(0.6, fixture.Stage(control: 0.5, steps: 10));
        JObject second = MakeClipWithRefs(
            [MakeRef("Base", frame: 2), MakeRef("Base", frame: 2, fromEnd: true)],
            fixture.Stage(control: 0.5, steps: 10));
        second["duration"] = 0.6;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVAddGuideNode[] guides = [.. bridge.Graph.NodesOfType<LTXVAddGuideNode>()];
        Assert.Equal([-2, 10], guides.Select(guide => guide.FrameIdx.LiteralAsInt()!.Value).Order());

        live.AssertAllLive(guides);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Carried audio conditions the next clip's sampler on the previous clip's decoded tail: the
    /// window is masked in at full strength over an empty target latent, without a re-encode.
    /// </summary>
    [Theory]
    [InlineData(Constants.BoundaryOutContinue, ContinueOverlap, 0.38)]
    [InlineData(Constants.BoundaryOutCrossfade, CrossfadeOverlap, 0.33)]
    public async Task Boundary_audio_carry_conditions_the_next_clip_sampler_from_the_previous_tail(
        string boundary,
        int carryFrames,
        double windowEndSeconds)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = MakeClip(0.6, fixture.Stage(control: 0.5, steps: 10));
        first["boundaryOut"] = boundary;
        first["boundaryOutCarryAudio"] = true;
        JObject second = MakeClip(0.6, fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstClip = StageSampler(bridge, 0);
        SwarmKSamplerNode secondClip = StageSampler(bridge, 1);
        SwarmSetAudioMaskWindowsNode carryMask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        Assert.Same(carryMask.Latent, JointLatentOf(secondClip).AudioLatent.Connection);
        Assert.Equal(1.0, carryMask.GapMaskValue.LiteralAsDouble());

        JArray windows = JArray.Parse(carryMask.Windows.LiteralAsString());
        JObject carryWindow = Assert.IsType<JObject>(Assert.Single(windows));
        Assert.Equal(0.0, carryWindow.Value<double>("start"), precision: 6);
        // Production rounds the window bounds to hundredths of a second.
        Assert.Equal(windowEndSeconds, carryWindow.Value<double>("end"), precision: 6);

        // The carry is a latent-space read of the previous clip's own audio channel, positioned at
        // the tail it overlaps — no decode/encode round trip.
        Assert.IsType<LTXVSeparateAVLatentNode>(carryMask.Samples.Connection!.Node);
        Assert.IsType<LTXVEmptyLatentAudioNode>(carryMask.TargetSamples.Connection!.Node);
        Assert.Equal(
            (ClipFrames - carryFrames) / 24.0,
            carryMask.SourceStartSeconds.LiteralAsDouble()!.Value,
            precision: 6);
        Assert.True(ReachesUpstream(bridge, carryMask.Samples.Connection.Node, firstClip.Id));
        Assert.True(ReachesUpstream(bridge, secondClip, carryMask.Id));
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());

        // Carry is generation-time context. There is no second merge near final publication.
        Assert.Empty(bridge.Graph.NodesOfType<AudioMergeNode>());
        Assert.IsType<AudioConcatNode>(live.PublishedAudio());

        live.AssertAllLive(firstClip, secondClip, carryMask);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// With no video model on the request the host root is a still image. The carry offset still
    /// comes out of the plan's own frame counts: 5.0s aligns to 121 frames, so the 9-frame carry
    /// starts at 112.
    /// </summary>
    [Fact]
    public async Task Boundary_audio_carry_uses_plan_timing_when_the_root_is_an_image()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = ContinueClip(5.0, fixture.Stage(control: 0.5, steps: 10));
        first["boundaryOutCarryAudio"] = true;
        JObject second = MakeClip(5.0, fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(first, second),
            post =>
            {
                post["model"] = fixture.BaseModel.Name;
                post.Remove("text2videoframes");
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstClip = StageSampler(bridge, 0);
        SwarmKSamplerNode secondClip = StageSampler(bridge, 1);

        // The root really is an image: clip 0 guides off core's base-image decode.
        VAEDecodeNode baseImage = BaseImage(bridge);
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(firstClip).VideoLatent.Connection?.Node),
            baseImage.Id));

        SwarmSetAudioMaskWindowsNode carryMask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        Assert.Same(carryMask.Latent, JointLatentOf(secondClip).AudioLatent.Connection);
        Assert.IsType<LTXVSeparateAVLatentNode>(carryMask.Samples.Connection!.Node);
        Assert.IsType<LTXVEmptyLatentAudioNode>(carryMask.TargetSamples.Connection!.Node);
        Assert.Equal(
            (LongClipFrames - ContinueOverlap) / 24.0,
            carryMask.SourceStartSeconds.LiteralAsDouble()!.Value,
            precision: 6);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());

        live.AssertAllLive(firstClip, secondClip, carryMask);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Every stage that regenerates the clip head re-applies the frozen tail, each at its own
    /// resolution. Clip 1 opens at 512 and upscales to 1024, so the same tail is conformed twice —
    /// and the 1024 anchor conforms straight from clip 0's output rather than from the 512 copy.
    /// </summary>
    [Fact]
    public async Task Continue_boundary_reanchors_the_tail_at_every_stage_that_regenerates_the_head()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = ContinueClip(
            0.6,
            fixture.Stage(control: 0.5, steps: 10),
            fixture.Stage(control: 0.5, steps: 10, upscale: 2.0));
        JObject second = MakeClip(
            0.6,
            fixture.Stage(control: 0.5, steps: 10),
            fixture.Stage(control: 0.5, steps: 10, upscale: 2.0));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode clipZeroLast = StageSampler(bridge, 1);
        ImageFromBatchNode tail = ContinuityTailSlice(bridge, ClipOutputDecodeId(bridge, clipZeroLast));

        List<LTXVImgToVideoInplaceNode> anchors = TailAnchors(bridge, tail.Id);
        Assert.Equal(2, anchors.Count);
        Assert.All(anchors, anchor => Assert.Equal(1.0, anchor.Strength.LiteralAsDouble()));
        Assert.Equal([(512, 512)], TailGuideScales(anchors[0], tail.Id));
        Assert.Equal([(1024, 1024)], TailGuideScales(anchors[1], tail.Id));
        Assert.True(
            ReachesUpstream(bridge, anchors[1], clipZeroLast.Id),
            "The final stage's anchor must trace back to clip 0's own output.");

        // Re-anchoring is invisible to the merge: the published slice geometry is unchanged.
        Assert.Equal(ContinueMergeSlices, MergeSlices(bridge));

        live.AssertAllLive([tail, clipZeroLast, .. anchors]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Control 0 with no retake and no scaling regenerates nothing, so it has no head to re-anchor
    /// and contributes no sampler at all. Only clip 1's first stage anchors the tail.
    /// </summary>
    [Fact]
    public async Task Passthrough_stage_adds_no_continuity_anchor()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = ContinueClip(0.6, fixture.Stage(control: 0.5, steps: 10));
        JObject second = MakeClip(
            0.6,
            fixture.Stage(control: 0.5, steps: 10),
            fixture.Stage(control: 0.0, steps: 10));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstClip = StageSampler(bridge, 0);
        ImageFromBatchNode tail = ContinuityTailSlice(bridge, ClipOutputDecodeId(bridge, firstClip));
        LTXVImgToVideoInplaceNode anchor = Assert.Single(TailAnchors(bridge, tail.Id));
        Assert.Same(anchor, JointLatentOf(StageSampler(bridge, 1)).VideoLatent.Connection?.Node);
        // The control-0 stage contributes no sampler of its own; a regression that emitted one
        // would seed it here and leave both lookups above still resolving.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => sampler.NoiseSeed.LiteralAsLong()
                == VideoStagesWorkflowFixture.StageSeed(2));

        live.AssertAllLive(firstClip, tail, anchor);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Adding a second stage to the target clip adds a second anchor but must not widen the
    /// overlap: identical merge geometry between the two runs pins the published timeline.
    /// </summary>
    [Fact]
    public async Task Reanchoring_a_later_stage_leaves_the_overlap_frame_count_untouched()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject singleWorkflow = await fixture.GenerateAsync(MakeDocument(
            ContinueClip(0.6, fixture.Stage(control: 0.5, steps: 10)),
            MakeClip(0.6, fixture.Stage(control: 0.5, steps: 10))));
        JObject reanchoredWorkflow = await fixture.GenerateAsync(MakeDocument(
            ContinueClip(0.6, fixture.Stage(control: 0.5, steps: 10)),
            MakeClip(
                0.6,
                fixture.Stage(control: 0.5, steps: 10),
                fixture.Stage(control: 0.5, steps: 10))));

        using WorkflowBridge singleBridge = WorkflowBridge.Create(singleWorkflow);
        using WorkflowBridge reanchoredBridge = WorkflowBridge.Create(reanchoredWorkflow);
        WorkflowLivePath singleLive = WorkflowLivePath.For(singleBridge);
        WorkflowLivePath reanchoredLive = WorkflowLivePath.For(reanchoredBridge);

        ImageFromBatchNode singleTail = ContinuityTailSlice(
            singleBridge,
            ClipOutputDecodeId(singleBridge, StageSampler(singleBridge, 0)));
        ImageFromBatchNode reanchoredTail = ContinuityTailSlice(
            reanchoredBridge,
            ClipOutputDecodeId(reanchoredBridge, StageSampler(reanchoredBridge, 0)));
        Assert.Single(TailAnchors(singleBridge, singleTail.Id));
        Assert.Equal(2, TailAnchors(reanchoredBridge, reanchoredTail.Id).Count);

        Assert.Equal(ContinueMergeSlices, MergeSlices(singleBridge));
        Assert.Equal(MergeSlices(singleBridge), MergeSlices(reanchoredBridge));
        Assert.Equal(
            ContinueOverlap,
            Assert.Single(reanchoredBridge.Graph.NodesOfType<SwarmRampMaskBatchNode>())
                .Frames.LiteralAsInt());

        singleLive.AssertAllLive(singleTail);
        AssertShippable(singleBridge, singleWorkflow, singleLive);
        reanchoredLive.AssertAllLive(reanchoredTail);
        AssertShippable(reanchoredBridge, reanchoredWorkflow, reanchoredLive);
    }

    /// <summary>
    /// An explicit first-frame reference on the target clip overrides the incoming continuity, so
    /// the boundary degrades to a plain cut and no stage has a tail to anchor. The reference itself
    /// is the positive control: without it, "nothing is anchored" would also be satisfied by a clip
    /// that built no guide at all.
    /// </summary>
    [Fact]
    public async Task Explicit_first_frame_reference_is_never_anchored_at_any_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = ContinueClip(0.6, fixture.Stage(control: 0.5, steps: 10));
        JObject second = MakeClipWithRefs(
            [MakeRef("Base", frame: 1)],
            fixture.Stage(control: 0.5, steps: 10),
            fixture.Stage(control: 0.5, steps: 10, upscale: 2.0));
        second["duration"] = 0.6;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Both of the target clip's stages guide off the base image, not off clip 0's tail.
        VAEDecodeNode baseImage = BaseImage(bridge);
        SwarmKSamplerNode firstClip = StageSampler(bridge, 0);
        LTXVImgToVideoInplaceNode[] targetGuides =
        [
            Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(StageSampler(bridge, 1)).VideoLatent.Connection?.Node),
            Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(StageSampler(bridge, 2)).VideoLatent.Connection?.Node),
        ];
        Assert.All(targetGuides, guide =>
        {
            Assert.True(ReachesUpstream(bridge, guide.Image.Connection?.Node, baseImage.Id));
            Assert.False(ReachesUpstream(bridge, guide.Image.Connection?.Node, firstClip.Id));
        });

        // A cut needs no tail slice and no seam blend.
        Assert.Empty(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        // Both clips publish their full length; nothing is consumed by an overlap.
        BatchImagesNodeNode batch = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Equal(2, batch.Images.Count);

        live.AssertAllLive([batch, .. targetGuides]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A crossfade overlaps decoded pixels only — no stage freezes the previous clip's tail as
    /// latent context, at any resolution.
    /// </summary>
    [Fact]
    public async Task Crossfade_boundary_anchors_nothing_at_any_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = MakeClip(0.6, fixture.Stage(control: 0.5, steps: 10));
        first["boundaryOut"] = Constants.BoundaryOutCrossfade;
        JObject second = MakeClip(
            0.6,
            fixture.Stage(control: 0.5, steps: 10),
            fixture.Stage(control: 0.5, steps: 10, upscale: 2.0));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        string clipZeroDecode = ClipOutputDecodeId(bridge, StageSampler(bridge, 0));
        // NotEmpty first: "no anchor" must mean the guides exist and none of them anchors, not that
        // the crossfade stopped building guides at all.
        IReadOnlyList<LTXVImgToVideoInplaceNode> guides =
            [.. bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()];
        Assert.NotEmpty(guides);
        Assert.All(guides, node => Assert.Null(TailGuideScales(node, clipZeroDecode)));

        ImageCompositeMaskedNode blend = Assert.Single(
            bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        Assert.Equal(
            CrossfadeOverlap,
            Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>()).Frames.LiteralAsInt());

        live.AssertAllLive(blend);
        AssertShippable(bridge, workflow, live);
    }
}
