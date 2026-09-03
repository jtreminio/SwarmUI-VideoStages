using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Generated;
using VideoStages.CharacterRefine;
using VideoStages.Execution;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2CharacterRefineContractTests
{
    [Fact]
    public async Task Character_refine_preflights_the_current_bonita_nodes()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = BonitaTestModels.CharacterRefineSettings();

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.Post(MakeDocument(SourceClip(0.6, 0, stage))),
                extraFeatures: []));

        Assert.Contains(BonitaFindAndCropNode.ClassType, error.Message);
        Assert.Contains(BonitaApplyRepairNode.ClassType, error.Message);
        Assert.Contains(BonitaSAM3FinderNode.ClassType, error.Message);
        Assert.Contains(BonitaYOLOFinderNode.ClassType, error.Message);
    }

    [Fact]
    public void Character_refine_rejects_an_image_after_only_passthrough_predecessors()
    {
        StageSpec passthrough = SpecFixtures.Stage(
            0,
            control: 0,
            clipStageIndex: 0,
            clipStageRawIndex: 0);
        StageSpec bonita = SpecFixtures.Stage(
            1,
            control: 0.5,
            clipStageIndex: 1,
            clipStageRawIndex: 1) with
        {
            CharacterRefine = new(
                [],
                false,
                "yolo",
                "person",
                "face",
                0.3,
                0.2,
                384,
                320),
        };

        ArchitectureClipCompilation compilation = Ltx2ClipPlanCompiler.Compile(
            SpecFixtures.Clip(0, [passthrough, bonita]),
            new(640, 360, 24, ArchitectureEntryMode.ImageToVideo));

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code == "ltx2.character-refine.decoded-input-required");
    }

    [Fact]
    public async Task Ltx25_refines_tracked_crops_and_composites_them_into_the_source_video()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.Model.ModelClass = fixture.Model.ModelClass with
        {
            ID = "lightricks-ltx-video-2-5",
        };
        fixture
            .InstallModel(
                "Clip",
                "gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot-v2.safetensors")
            .InstallModel(
                "VAE",
                CommonModels.Known["ltx2-5-video-vae"].FileName)
            .InstallModel(
                "VAE",
                CommonModels.Known["ltx2-5-audio-vae"].FileName);
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = BonitaTestModels.CharacterRefineSettings();

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(SourceClip(0.6, 0, stage))),
                extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BonitaYOLOFinderNode finder = Assert.Single(
            bridge.Graph.NodesOfType<BonitaYOLOFinderNode>());
        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
        BonitaApplyRepairNode finish = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        SwarmKSamplerNode sampler = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>());

        Assert.Equal("lightricks-ltx-video-2-5", fixture.Model.ModelClass.ID);
        Assert.Same(finder, crop.Finder.Connection?.Node);
        Assert.Equal(384, crop.CropWidth.LiteralAsInt());
        Assert.Equal(320, crop.CropHeight.LiteralAsInt());
        Assert.Same(crop.VideoFrames.Connection, finish.VideoFrames.Connection);
        Assert.True(ReachesUpstream(bridge, sampler, crop.Id));
        Assert.True(ReachesUpstream(bridge, finish, sampler.Id));
        Assert.Same(crop, finish.PlacementData.Connection?.Node);
        Assert.Equal(4, sampler.Steps.LiteralAsInt());
        Assert.Equal(2, sampler.StartAtStep.LiteralAsInt());
        SwarmFrameWindowNode pad = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>(),
            node => ReferenceEquals(node.ImagesInput.Connection?.Node, crop));
        Assert.Equal(17, pad.FrameCount.LiteralAsInt());
        SwarmFrameWindowNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>(),
            node => ReachesUpstream(bridge, node, sampler.Id)
                && node.FrameCount.LiteralAsInt() == 16);
        Assert.Same(trim, finish.RepairedCrops.Connection?.Node);
        Assert.Equal(16, generator.CurrentMedia.Frames);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task Character_refine_uses_the_seed_carried_by_a_refine_video_source()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject clip = SourceClip(0.6, 0, stage);
        JObject post = fixture.Post(MakeDocument(clip));
        post["extra_metadata"] = new JObject
        {
            [VideoSamplerSeed.MetadataKey] = "987",
        };

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            post,
            extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmKSamplerNode sampler = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(987L, sampler.NoiseSeed.LiteralAsLong());
    }

    [Fact]
    public async Task Later_clip_character_refine_does_not_reuse_an_earlier_clips_seed()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject firstClip = MakeClip(
            0.6,
            fixture.Stage(control: 0.5, steps: 4));
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject secondClip = SourceClip(0.6, 0, bonita);

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(firstClip, secondClip)),
            extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BonitaApplyRepairNode finish = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        SwarmKSamplerNode bonitaSampler = bridge.Graph.FindNearestUpstream<SwarmKSamplerNode>(
            finish.RepairedCrops.Connection?.Node);
        Assert.Equal(
            VideoStagesWorkflowFixture.StageSeed(1),
            bonitaSampler.NoiseSeed.LiteralAsLong());
    }

    [Fact]
    public async Task Completed_video_records_a_large_sampler_seed_as_lossless_output_metadata()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        const long requestSeed = 9_007_199_254_740_993;
        JObject post = fixture.Post(MakeDocument(SourceClip(0.6, 0, stage)));
        post["seed"] = requestSeed;

        (_, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                post);

        Assert.Equal(
            $"{requestSeed + 42}",
            generator.UserInput.ExtraMeta[VideoSamplerSeed.MetadataKey]);
    }

    [Theory]
    [InlineData("guide")]
    [InlineData("audio-duration")]
    [InlineData("control-duration")]
    public async Task Character_refine_rejects_incompatible_stage_guide_or_dynamic_inputs(
        string incompatible)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject clip = SourceClip(0.6, 0, stage);
        switch (incompatible)
        {
            case "guide":
                stage["imageReference"] = "Base";
                break;
            case "audio-duration":
                clip["audioSource"] = MediaSource.Upload;
                clip["uploadedAudio"] = UploadedAudio();
                clip["clipLengthFromAudio"] = true;
                break;
            case "control-duration":
                clip["clipLengthFromControlNet"] = true;
                break;
        }

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)),
                extraFeatures: BonitaTestModels.Features()));

        Assert.Contains(incompatible switch
        {
            "guide" => "cannot use a stage image guide",
            _ => "requires a fixed clip duration",
        }, error.Message);
    }

    [Fact]
    public async Task Character_refine_ignores_an_ic_lora_with_zero_stage_strength()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");
        JObject first = fixture.Stage(control: 0.5, steps: 4);
        first["icLoraStrengths"] = new JArray(1.0);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        bonita["icLoraStrengths"] = new JArray(0.0);
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject clip = SourceClip(0.6, 0, first, bonita);
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "UnitTest_IcLoraA",
            ["driveSource"] = MediaSource.Upload,
            ["driveData"] = $"{IcLoraDriveData.Visual}",
            ["strength"] = 1.0,
            ["attentionStrength"] = 1.0,
            ["controlType"] = Constants.IcLoraControlNone,
            ["driveMedia"] = new JObject
            {
                ["data"] = "data:video/mp4;base64,QUJD",
                ["fileName"] = "drive.mp4",
            },
        });

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(clip)),
            extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
    }

    [Fact]
    public async Task Bonita_stage_ignores_keyframes_after_the_full_video_pass()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject fullFrame = fixture.Stage(control: 0.5, steps: 4);
        fullFrame["keyframeStrengths"] = new JArray(0.55);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        bonita["keyframeStrengths"] = new JArray(0.75);
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject clip = MakeClipWithRefs([MakeRef("Base", frame: 2)], fullFrame, bonita);
        clip["duration"] = 1.0;

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.ImageToVideoPost(MakeDocument(clip)),
            extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVAddGuideNode[] guides =
            [.. bridge.Graph.NodesOfType<LTXVAddGuideNode>()];
        LTXVAddGuideNode fullFrameGuide = Assert.Single(guides);
        Assert.Equal(0.55, fullFrameGuide.Strength.LiteralAsDouble());
        SwarmKSamplerNode fullFrameSampler = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => ReferenceEquals(sampler.Positive.Connection?.Node, fullFrameGuide));
        BonitaApplyRepairNode finish = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        SwarmKSamplerNode cropSampler = bridge.Graph.FindNearestUpstream<SwarmKSamplerNode>(
            finish.RepairedCrops.Connection?.Node);
        Assert.Equal(
            fullFrameSampler.NoiseSeed.LiteralAsLong(),
            cropSampler.NoiseSeed.LiteralAsLong());

        Assert.DoesNotContain(
            workflow.Properties(),
            property => $"{property.Value["class_type"]}" == "SwarmBonitaCropGuide");
        Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task Bonita_stage_does_not_validate_an_ignored_uploaded_keyframe()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject clip = SourceClip(0.6, 0, bonita);
        clip["keyframes"] = new JArray(UploadedReference("@@@"));

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.ImageToVideoPost(MakeDocument(clip)),
            extraFeatures: BonitaTestModels.Features());

        Assert.Contains(
            workflow.Properties(),
            property => $"{property.Value["class_type"]}" == BonitaApplyRepairNode.ClassType);
    }

    [Fact]
    public async Task Host_video_identity_reference_is_reduced_to_one_image()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        JObject settings = BonitaTestModels.CharacterRefineSettings();
        settings["verifyIdentity"] = true;
        stage["characterRefine"] = settings;
        JObject clip = SourceClip(0.6, 0, stage);
        JObject reference = MakeClipReference("image", source: "edit0");
        reference["id"] = "identity";
        clip["references"] = new JArray(reference);
        BuffaloModelManifest prior = BonitaIntegration.ReplaceBuffaloManifestForTests(
            new(new("unused.zip", "https://invalid", "00"), []));
        try
        {
            using IDisposable models = BonitaTestModels.InstallYoloModels();
            JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)),
                extraFeatures: BonitaTestModels.Features(),
                extraSteps: [PublishBase2EditVideo(0)]);
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

            BonitaFindAndCropNode crop = Assert.Single(
                bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
            ImageFromBatchNode first = Assert.IsType<ImageFromBatchNode>(
                crop.CharacterReference.Connection?.Node);
            Assert.Equal(0, first.BatchIndex.LiteralAsInt());
            Assert.Equal(1, first.Length.LiteralAsInt());
            Assert.IsType<RepeatImageBatchNode>(first.Image.Connection?.Node);
        }
        finally
        {
            BonitaIntegration.ReplaceBuffaloManifestForTests(prior);
        }
    }

    [Fact]
    public async Task Uploaded_identity_reference_is_not_wrapped_as_a_video_batch()
    {
        const string image =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ"
            + "AAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        JObject settings = BonitaTestModels.CharacterRefineSettings();
        settings["verifyIdentity"] = true;
        stage["characterRefine"] = settings;
        JObject clip = SourceClip(0.6, 0, stage);
        JObject reference = MakeClipReference("image", image, "identity.png");
        reference["id"] = "identity";
        clip["references"] = new JArray(reference);
        BuffaloModelManifest prior = BonitaIntegration.ReplaceBuffaloManifestForTests(
            new(new("unused.zip", "https://invalid", "00"), []));
        try
        {
            using IDisposable models = BonitaTestModels.InstallYoloModels();
            JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)),
                extraFeatures: BonitaTestModels.Features());
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

            BonitaFindAndCropNode crop = Assert.Single(
                bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
            Assert.IsType<SwarmLoadImageB64Node>(crop.CharacterReference.Connection?.Node);
        }
        finally
        {
            BonitaIntegration.ReplaceBuffaloManifestForTests(prior);
        }
    }

    [Fact]
    public async Task A_following_ltx_stage_reads_the_bonita_composite()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = fixture.Stage(control: 0.5, steps: 4);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject following = fixture.Stage(control: 0.5, steps: 4);

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(SourceClip(0.6, 0, first, bonita, following))),
            extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BonitaApplyRepairNode composite = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        SwarmKSamplerNode followingSampler = StageSampler(bridge, 2);
        Assert.True(ReachesUpstream(bridge, followingSampler, composite.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData("pixel-lanczos")]
    [InlineData("model-unit-test-upscaler")]
    public async Task A_following_ltx_stage_reads_the_upscaled_bonita_composite(
        string upscaleMethod)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = fixture.Stage(control: 0.5, steps: 4);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject upscale = fixture.Stage(
            control: 0,
            upscale: 2,
            upscaleMethod: upscaleMethod,
            steps: 4);
        JObject following = fixture.Stage(control: 0.5, steps: 4);

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(SourceClip(
                0.6,
                0,
                first,
                bonita,
                upscale,
                following))),
            extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BonitaApplyRepairNode composite = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        SwarmKSamplerNode followingSampler = StageSampler(bridge, 3);
        Assert.True(ReachesUpstream(bridge, followingSampler, composite.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task A_second_following_ltx_stage_reuses_the_fresh_stage_latent()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = fixture.Stage(control: 0.5, steps: 4);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject regenerated = fixture.Stage(control: 0.5, steps: 4);
        JObject following = fixture.Stage(control: 0.5, steps: 4);

        using IDisposable models = BonitaTestModels.InstallYoloModels();
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(SourceClip(
                0.6,
                0,
                first,
                bonita,
                regenerated,
                following))),
            extraFeatures: BonitaTestModels.Features());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmKSamplerNode regeneratedSampler = StageSampler(bridge, 2);
        SwarmKSamplerNode followingSampler = StageSampler(bridge, 3);
        Assert.Same(
            OutputOf(bridge, regeneratedSampler).VideoLatent,
            JointLatentOf(followingSampler).VideoLatent.Connection);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task A_continue_target_rejects_character_refine()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject lead = MakeClip(0.6, fixture.Stage(control: 0.5, steps: 4));
        lead["boundaryOut"] = Constants.BoundaryOutContinue;
        JObject targetFirst = fixture.Stage(control: 0.5, steps: 4);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        bonita["characterRefine"] = BonitaTestModels.CharacterRefineSettings();
        JObject target = MakeClip(0.6, targetFirst, bonita);

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.Post(MakeDocument(lead, target)),
                extraFeatures: BonitaTestModels.Features()));

        Assert.Contains("Continue-boundary target", error.Message);
    }

    [Fact]
    public async Task Missing_controlnet_identity_is_an_authored_input_error()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        JObject settings = BonitaTestModels.CharacterRefineSettings();
        settings["verifyIdentity"] = true;
        stage["characterRefine"] = settings;
        JObject clip = SourceClip(0.6, 0, stage);
        JObject reference = MakeClipReference("image", source: MediaSource.ControlNetOne);
        reference["id"] = "identity";
        clip["references"] = new JArray(reference);

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)),
                extraFeatures: BonitaTestModels.Features()));

        Assert.Contains("ControlNet", error.Message);
        Assert.Contains("unavailable", error.Message);
    }

    [Fact]
    public async Task Missing_host_stage_identity_is_an_authored_input_error()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = fixture.Stage(control: 0.5, steps: 4);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        JObject settings = BonitaTestModels.CharacterRefineSettings();
        settings["verifyIdentity"] = true;
        bonita["characterRefine"] = settings;
        JObject clip = MakeClip(0.6, first, bonita);
        JObject reference = MakeClipReference("image", source: MediaSource.Base);
        reference["id"] = "identity";
        clip["references"] = new JArray(reference);

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.Post(MakeDocument(clip)),
                extraFeatures: BonitaTestModels.Features()));

        Assert.Contains("Base", error.Message);
        Assert.Contains("unavailable", error.Message);
    }

    [Fact]
    public async Task Identity_resolution_skips_an_unavailable_host_stage_reference()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = fixture.Stage(control: 0.5, steps: 4);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita.Remove("imageReference");
        JObject settings = BonitaTestModels.CharacterRefineSettings();
        settings["verifyIdentity"] = true;
        bonita["characterRefine"] = settings;
        JObject clip = MakeClip(0.6, first, bonita);
        JObject missing = MakeClipReference("image", source: MediaSource.Base);
        missing["id"] = "missing";
        JObject available = MakeClipReference("image", source: "edit0");
        available["id"] = "available";
        clip["references"] = new JArray(missing, available);
        BuffaloModelManifest prior = BonitaIntegration.ReplaceBuffaloManifestForTests(
            new(new("unused.zip", "https://invalid", "00"), []));
        try
        {
            using IDisposable models = BonitaTestModels.InstallYoloModels();
            JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.Post(MakeDocument(clip)),
                extraFeatures: BonitaTestModels.Features(),
                extraSteps: [PublishBase2EditImageStep(0)]);
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

            BonitaFindAndCropNode crop = Assert.Single(
                bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
            Assert.IsType<SwarmLoadImageB64Node>(crop.CharacterReference.Connection?.Node);
        }
        finally
        {
            BonitaIntegration.ReplaceBuffaloManifestForTests(prior);
        }
    }

    private static WorkflowGenerator.WorkflowGenStep PublishBase2EditVideo(int stage) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            SwarmLoadImageB64Node load = bridge.AddNode(
                new SwarmLoadImageB64Node().With(
                    ImageBase64: "data:image/png;base64,QUJD"));
            RepeatImageBatchNode batch = bridge.AddNode(
                new RepeatImageBatchNode().With(Amount: 3));
            batch.Image.ConnectTo(load.IMAGE);
            JObject media = new()
            {
                ["path"] = batch.IMAGE.ToPath(),
                ["dataType"] = WGNodeData.DT_VIDEO,
                ["width"] = VideoStagesWorkflowFixture.Width,
                ["height"] = VideoStagesWorkflowFixture.Height,
                ["frames"] = 3,
            };
            g.NodeHelpers[$"b2e.published.edit.{stage}"] = new JObject
            {
                ["media"] = media,
            }.ToString();
        }, Constants.WorkflowStepPriority.ApplyRootAudioMaskDimensions);

}
