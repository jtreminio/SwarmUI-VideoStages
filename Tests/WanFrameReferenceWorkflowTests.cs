using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Wan's native first/last frame conditioning: which stage owns each endpoint, and how a clip-local
/// upload interacts with the request's global end image.
/// </summary>
[Collection("VideoStagesTests")]
public class WanFrameReferenceWorkflowTests
{
    // ---- authored frame references ------------------------------------------------------

    /// <summary>
    /// An uploaded first-frame reference gives a text-to-video clip a donor of its own, so it
    /// conditions through <c>WanImageToVideo</c> rather than sampling core's empty latent.
    /// </summary>
    [Fact]
    public async Task An_uploaded_first_frame_reference_conditions_a_text_root()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["frameRefs"] = new JArray(UploadedReference("RklSU1Q="));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("RklSU1Q=", upload.ImageBase64.LiteralAsString());
        WanImageToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Same(
            upload,
            WanWorkflowFixture.FirstFrameFraming(conditioning.StartImage.Connection?.Node).Image.Connection?.Node);

        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(conditioning.Latent, stage.LatentImage.Connection);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyHunyuanLatentVideoNode>());

        live.AssertAllLive(upload, conditioning, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An unusable first-frame reference blocks the request at preflight. Silently falling back to
    /// the empty latent would generate a clip that ignores the frame it was authored to start on.
    /// </summary>
    [Theory]
    [InlineData(false, "missing inline data and a file name")]
    [InlineData(true, "not a readable image")]
    public async Task An_unusable_first_frame_reference_rejects_the_request(
        bool malformed,
        string expectedError)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create();
        JObject reference = MakeRef("Upload");
        if (malformed)
        {
            reference["uploadedImage"] = new JObject
            {
                ["data"] = "not-an-image-payload",
                ["fileName"] = "broken.png",
            };
        }
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["frameRefs"] = new JArray(reference);

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => fixture.GenerateAsync(MakeDocument(clip)));

        Assert.Contains(expectedError, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A last-only reference routes to the first/last conditioning node with no start image at
    /// all. Wan 2.1 encodes the end frame for CLIP-vision as well; Wan 2.2 does not.
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, false)]
    [InlineData(WanWorkflowFixture.Wan21I2v14bFixturePath, true)]
    public async Task An_uploaded_last_frame_reference_conditions_a_text_root_without_a_donor(
        string modelFixturePath,
        bool expectsClipVision)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create(modelFixturePath);
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["frameRefs"] = new JArray(UploadedReference("TEFTVA==", fromEnd: true));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        WanFirstLastFrameToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.False(conditioning.StartImage.HasValue);
        Assert.False(conditioning.ClipVisionStartImage.HasValue);
        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());
        // The end frame is framed but never unwrapped to one frame — it already is one.
        ImageScaleNode framing = Assert.IsType<ImageScaleNode>(
            conditioning.EndImage.Connection?.Node);
        Assert.Same(upload, framing.Image.Connection?.Node);

        if (expectsClipVision)
        {
            CLIPVisionEncodeNode vision = Assert.IsType<CLIPVisionEncodeNode>(
                conditioning.ClipVisionEndImage.Connection?.Node);
            Assert.Same(framing.IMAGE, vision.Image.Connection);
            live.AssertLive(vision);
        }
        else
        {
            Assert.False(conditioning.ClipVisionEndImage.HasValue);
            Assert.Empty(bridge.Graph.NodesOfType<CLIPVisionEncodeNode>());
        }

        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<EmptyHunyuanLatentVideoNode>());
        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(conditioning.Latent, stage.LatentImage.Connection);

        live.AssertAllLive(upload, conditioning, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A malformed first reference fails the whole request even when the clip's other reference is
    /// fine: preflight refuses to generate a clip that silently drops an authored keyframe.
    /// </summary>
    [Fact]
    public async Task A_malformed_first_reference_rejects_the_request()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["frameRefs"] = new JArray(
            UploadedReference("not-valid-base64"),
            UploadedReference("TEFTVA==", fromEnd: true));

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => fixture.GenerateAsync(MakeDocument(clip)));

        Assert.Contains("first frame image", error.Message, StringComparison.Ordinal);
        // The valid last reference must not contribute an error of its own, which is the whole
        // "only the broken one is at fault" claim.
        Assert.DoesNotContain("last frame image", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clip's last-frame reference belongs to the last stage that actually generates. Stage 0
    /// conditions on the host image alone, stage 1 owns the end frame, and stage 2 — authored at
    /// <c>control: 0</c> — is a passthrough with no sampler for the reference to land on.
    /// </summary>
    [Fact]
    public async Task An_uploaded_last_frame_reference_belongs_to_the_terminal_generating_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12),
            fixture.Stage("PreviousStage", control: 0, steps: 13));
        clip["frameRefs"] = new JArray(UploadedReference("TEFTVA==", fromEnd: true));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());
        WanImageToVideoNode opening = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        WanFirstLastFrameToVideoNode terminal = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.True(ReachesUpstream(bridge, terminal.EndImage.Connection?.Node, upload.Id));

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Same(opening.Positive, first.Positive.Connection);
        Assert.Same(terminal.Positive, second.Positive.Connection);
        Assert.Equal(10, first.Steps.LiteralAsInt());
        Assert.Equal(12, second.Steps.LiteralAsInt());
        // The passthrough stage contributes no pass of its own; core's base sampler is the third.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => sampler.Steps.LiteralAsInt() == 13);

        live.AssertAllLive(upload, opening, terminal, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Clip-local references are the clip's own business: with both authored, the request's global
    /// end image is neither loaded into the graph nor consumed off the request.
    /// </summary>
    [Fact]
    public async Task Clip_local_uploads_leave_the_requests_global_end_image_untouched()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["frameRefs"] = new JArray(
            UploadedReference("RklSU1Q="),
            UploadedReference("TEFTVA==", fromEnd: true));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(clip), post => post["videoendimage"] = WanWorkflowFixture.EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node[] uploads = [.. bridge.Graph.NodesOfType<SwarmLoadImageB64Node>()];
        Assert.Equal(
            ["RklSU1Q=", "TEFTVA=="],
            uploads.Select(upload => upload.ImageBase64.LiteralAsString()).Order());
        WanFirstLastFrameToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.StartImage.Connection?.Node,
            Assert.Single(uploads, upload =>
                upload.ImageBase64.LiteralAsString() == "RklSU1Q=").Id));
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.EndImage.Connection?.Node,
            Assert.Single(uploads, upload =>
                upload.ImageBase64.LiteralAsString() == "TEFTVA==").Id));

        // The global end image really was accepted by the request — it is simply never used, and
        // the extension leaves it on the input rather than consuming it. Its payload is what proves
        // that: the two uploads above are the clip's own, and the request's is nowhere in the graph.
        Assert.NotNull(generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null));
        Assert.DoesNotContain(
            uploads,
            upload => upload.ImageBase64.LiteralAsString() == WanWorkflowFixture.EndImageBase64);

        live.AssertAllLive(conditioning, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The request's global end image goes to the clip's last generating stage and nowhere else:
    /// the opening stage keeps plain image-to-video conditioning off the host base image. Core's
    /// own WAN video root, which would carry a third conditioning node, is pruned.
    /// <para>
    /// The 2.1 arm exists because 2.1 additionally CLIP-vision encodes both ends; without that
    /// assertion it would run the same checks as its 2.2 sibling and pin nothing about the model.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, 0.5, false)]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, 1, false)]
    [InlineData(WanWorkflowFixture.Wan21I2v14bFixturePath, 1, true)]
    public async Task The_global_end_image_belongs_to_the_clips_terminal_generating_stage(
        string modelFixturePath,
        double terminalControl,
        bool expectsClipVision)
    {
        using WanWorkflowFixture fixture =
            WanWorkflowFixture.CreateWithBaseModel(modelFixturePath);
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: terminalControl, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["videoendimage"] = WanWorkflowFixture.EndImagePayload;
                    // Non-square on purpose: ImageScale defaults to 512x512, so the framing
                    // assertion below would hold at the fixture's own resolution either way.
                    post["width"] = 768;
                    post["height"] = 448;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode terminal = StageSampler(bridge, 1);
        WanImageToVideoNode opening = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        WanFirstLastFrameToVideoNode last = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Same(opening.Positive, first.Positive.Connection);
        Assert.Same(last.Positive, terminal.Positive.Connection);

        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(WanWorkflowFixture.EndImageBase64, endImage.ImageBase64.LiteralAsString());
        ImageScaleNode framing = Assert.IsType<ImageScaleNode>(last.EndImage.Connection?.Node);
        Assert.Same(endImage, framing.Image.Connection?.Node);
        Assert.Equal(768, framing.Width.LiteralAsInt());
        Assert.Equal(448, framing.Height.LiteralAsInt());
        Assert.Equal("lanczos", framing.UpscaleMethod.LiteralAsString());

        // The other half of "and nowhere else": the end image claims the end slot only. The start
        // stays the host base image, which both conditioning nodes share.
        SwarmKSamplerNode basePass = fixture.BaseSampler(bridge);
        Assert.True(ReachesUpstream(bridge, last.StartImage.Connection?.Node, basePass.Id));
        Assert.False(ReachesUpstream(bridge, last.StartImage.Connection?.Node, endImage.Id));
        Assert.True(ReachesUpstream(bridge, opening.StartImage.Connection?.Node, basePass.Id));
        Assert.False(ReachesUpstream(bridge, opening.StartImage.Connection?.Node, endImage.Id));

        if (expectsClipVision)
        {
            Assert.True(ReachesUpstream(
                bridge, last.ClipVisionEndImage.Connection?.Node, endImage.Id));
            Assert.True(ReachesUpstream(
                bridge, last.ClipVisionStartImage.Connection?.Node, basePass.Id));
        }
        else
        {
            Assert.False(last.ClipVisionEndImage.HasValue);
            Assert.False(last.ClipVisionStartImage.HasValue);
            Assert.Empty(bridge.Graph.NodesOfType<CLIPVisionEncodeNode>());
        }

        if (terminalControl < 1)
        {
            VAEEncodeNode refine = Assert.IsType<VAEEncodeNode>(
                terminal.LatentImage.Connection?.Node);
            Assert.True(ReachesUpstream(bridge, refine, first.Id));
            Assert.False(ReachesUpstream(bridge, refine, last.Id));
        }
        else
        {
            Assert.Same(last.Latent, terminal.LatentImage.Connection);
        }
        // The request keeps the end image; the extension consumes it per stage, not off the input.
        Assert.NotNull(generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null));
        Assert.True(ReachesUpstream(
            bridge, live.FinalVideoSave().Images.Connection?.Node, terminal.Id));

        live.AssertAllLive(endImage, last, first, terminal);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A trailing passthrough stage does not own the clip's end: the global end image goes to the
    /// last stage that samples, and the passthrough adds nothing after it.
    /// </summary>
    [Fact]
    public async Task A_trailing_passthrough_does_not_take_the_global_end_image()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12),
            fixture.Stage("PreviousStage", control: 0, steps: 13)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["videoendimage"] = WanWorkflowFixture.EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode terminal = StageSampler(bridge, 1);
        Assert.Single(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        WanFirstLastFrameToVideoNode last = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Same(last.Positive, terminal.Positive.Connection);
        // The stage that owns the end really did receive it, in the end slot only — without this
        // the test proves only that a first/last node exists somewhere.
        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(WanWorkflowFixture.EndImageBase64, endImage.ImageBase64.LiteralAsString());
        Assert.True(ReachesUpstream(bridge, last.EndImage.Connection?.Node, endImage.Id));
        Assert.True(ReachesUpstream(
            bridge, last.StartImage.Connection?.Node, fixture.BaseSampler(bridge).Id));
        Assert.False(ReachesUpstream(bridge, last.StartImage.Connection?.Node, endImage.Id));
        // Core's base pass plus the two generating stages; the passthrough contributes none.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => sampler.Steps.LiteralAsInt() == 13);
        Assert.True(ReachesUpstream(
            bridge, bridge.ResolvePath(generator.CurrentMedia.Path)?.Node, terminal.Id));

        live.AssertAllLive(last, terminal);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A clip's final-frame reference only claims the end-image slot once it materializes. A
    /// <c>Base</c> reference is dropped during planning and an upload with no payload is refused at
    /// runtime; either way the request's own end image is what conditions the clip.
    /// </summary>
    [Fact]
    public async Task An_unusable_last_reference_leaves_the_global_end_image_in_place()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage(control: 1, steps: 10));
        clip["frameRefs"] = new JArray(MakeRef("Base", fromEnd: true));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(clip), post => post["videoendimage"] = WanWorkflowFixture.EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        WanFirstLastFrameToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(WanWorkflowFixture.EndImageBase64, endImage.ImageBase64.LiteralAsString());
        Assert.True(ReachesUpstream(bridge, conditioning.EndImage.Connection?.Node, endImage.Id));
        // The end slot is all it claims: the start is still the host base image.
        Assert.True(ReachesUpstream(
            bridge, conditioning.StartImage.Connection?.Node, fixture.BaseSampler(bridge).Id));
        Assert.False(ReachesUpstream(bridge, conditioning.StartImage.Connection?.Node, endImage.Id));
        // Planning drops the Base reference and says so once. A second reference warning would
        // mean the request's own end image was questioned too.
        Assert.Single(
            Diagnostics(generator),
            diagnostic =>
                diagnostic.Code == "effective-request.wan-frame-reference-source-ignored");

        live.AssertAllLive(endImage, conditioning, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A final-frame upload with no payload at all is a blocking preflight error, not a quiet
    /// handover to the request's own end image.
    /// </summary>
    [Fact]
    public async Task A_payload_less_last_reference_upload_rejects_the_request()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage(control: 1, steps: 10));
        clip["frameRefs"] = new JArray(MakeRef("Upload", fromEnd: true));

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(clip), post => post["videoendimage"] = WanWorkflowFixture.EndImagePayload)));

        Assert.Contains("last frame image", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "missing inline data and a file name", error.Message, StringComparison.Ordinal);
    }
}
