using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.CharacterRefine;
using VideoStages.Execution;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxCharacterRefineContractTests
{
    [Fact]
    public async Task Character_refine_uses_the_current_bonita_workflow_api()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        JObject characterRefine = new()
        {
            ["pictureReferenceIds"] = new JArray("identity-ref", "context-ref"),
            ["verifyIdentity"] = true,
            ["finder"] = "sam3",
            ["subject"] = "animal",
            ["part"] = "hand",
            ["subjectConfidence"] = 0.2,
            ["partConfidence"] = 0.15,
            ["cropWidth"] = 768,
            ["cropHeight"] = 640,
        };
        stage["characterRefine"] = characterRefine;

        (JObject workflow, _) = await Generate(fixture, InitVideoClip(stage));
        JProperty finder = Assert.Single(workflow.Properties(), property =>
            IsNode(property, "BonitaSAM3Finder"));
        JProperty find = Assert.Single(workflow.Properties(), property =>
            IsNode(property, "BonitaFindAndCrop"));
        JProperty apply = Assert.Single(workflow.Properties(), property =>
            IsNode(property, "BonitaApplyRepair"));

        Assert.Equal(0.2, finder.Value["inputs"]?["subject_confidence"]?.Value<double>());
        Assert.Equal(0.15, finder.Value["inputs"]?["part_confidence"]?.Value<double>());
        Assert.Equal("balanced", finder.Value["inputs"]?["performance"]?.Value<string>());
        Assert.Equal(new JArray(finder.Name, 0), find.Value["inputs"]?["finder"]);
        Assert.NotNull(find.Value["inputs"]?["character_reference"]);
        Assert.Equal("animal", find.Value["inputs"]?["subject"]?.Value<string>());
        Assert.Equal("hand", find.Value["inputs"]?["part"]?.Value<string>());
        Assert.Equal(768, find.Value["inputs"]?["crop_width"]?.Value<int>());
        Assert.Equal(640, find.Value["inputs"]?["crop_height"]?.Value<int>());
        Assert.Equal(2.5, find.Value["inputs"]?["surrounding_area"]?.Value<double>());
        Assert.Equal(10, find.Value["inputs"]?["tracked_area_blur"]?.Value<int>());
        Assert.Equal("off", find.Value["inputs"]?["performance_report"]?.Value<string>());
        Assert.Equal(1.0, apply.Value["inputs"]?["small_detail_strength"]?.Value<double>());
        Assert.Equal(0.65, apply.Value["inputs"]?["medium_detail_strength"]?.Value<double>());
        Assert.Equal(0.15, apply.Value["inputs"]?["closeup_strength"]?.Value<double>());
        Assert.Equal(5, apply.Value["inputs"]?["strength_smoothing_frames"]?.Value<int>());
        Assert.Equal(0, apply.Value["inputs"]?["mask_expand_pixels"]?.Value<int>());
        Assert.Equal(0, apply.Value["inputs"]?["mask_feather_pixels"]?.Value<int>());
        Assert.Equal(0.5, apply.Value["inputs"]?["color_match_strength"]?.Value<double>());
        Assert.Equal("off", apply.Value["inputs"]?["performance_report"]?.Value<string>());
        Assert.Null(apply.Value["inputs"]?["repair_masks"]);
        Assert.Equal(
            new JArray(find.Name, 2),
            apply.Value["inputs"]?["placement_data"]);
        Assert.DoesNotContain(workflow.Properties(), property =>
            property.Value["class_type"]?.Value<string>() is
                "SAM3_VideoTrack" or "SAM3_Detect" or "MaskComposite");
    }

    [Fact]
    public async Task Yolo_refine_can_skip_identity_and_send_only_checked_pictures_to_h3()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = new JObject
        {
            ["pictureReferenceIds"] = new JArray("context-ref"),
            ["verifyIdentity"] = false,
            ["finder"] = "yolo",
            ["subjectConfidence"] = 0.3,
            ["partConfidence"] = 0.2,
            ["cropWidth"] = 768,
            ["cropHeight"] = 640,
        };

        (JObject workflow, _) = await Generate(fixture, InitVideoClip(stage));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        BonitaYOLOFinderNode finder = Assert.Single(
            bridge.Graph.NodesOfType<BonitaYOLOFinderNode>());
        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
        SwarmMiniMaxH3CollectReferencesNode reference = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3CollectReferencesNode>());

        Assert.Empty(bridge.Graph.NodesOfType<BonitaSAM3FinderNode>());
        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        Assert.Equal(
            BonitaIntegration.YoloSubjectModel,
            finder.SubjectModel.LiteralAsString());
        Assert.Equal(
            BonitaIntegration.YoloPartModel,
            finder.PartModel.LiteralAsString());
        Assert.Equal(0.3, finder.SubjectConfidence.LiteralAsDouble().Value, 5);
        Assert.Equal(0.2, finder.PartConfidence.LiteralAsDouble().Value, 5);
        Assert.Same(finder, crop.Finder.Connection?.Node);
        Assert.Null(crop.CharacterReference.Connection);
        Assert.Equal(768, crop.CropWidth.LiteralAsInt());
        Assert.Equal(640, crop.CropHeight.LiteralAsInt());
        Assert.Single(reference.RefImages.Items);
    }

    [Fact]
    public async Task Character_refine_wires_bonita_tracking_and_core_refinement_paths()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        JObject characterRefine = CharacterRefine("identity-ref", "context-ref");
        characterRefine["subject"] = "person";
        characterRefine["part"] = "face";
        characterRefine["subjectConfidence"] = 0.2;
        characterRefine["partConfidence"] = 0.1;
        stage["characterRefine"] = characterRefine;

        (JObject workflow, WorkflowGenerator generator) = await Generate(
            fixture,
            InitVideoClip(stage),
            post => post["prompt"] = "global <videoclip[0,0]>refine this character");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        CheckpointLoaderSimpleNode samLoader = Assert.Single(
            bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        BonitaSAM3FinderNode finder = Assert.Single(
            bridge.Graph.NodesOfType<BonitaSAM3FinderNode>());
        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
        BonitaApplyRepairNode finish = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        SwarmKSamplerNode sampler = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>());

        Assert.Equal(
            BonitaIntegration.SamCheckpointRelativePath,
            samLoader.CkptName.LiteralAsString());
        Assert.Same(samLoader, finder.Sam3Model.Connection?.Node);
        Assert.Same(samLoader, finder.Sam3Clip.Connection?.Node);
        Assert.Equal(0.2, finder.SubjectConfidence.LiteralAsDouble().Value, 5);
        Assert.Equal(0.1, finder.PartConfidence.LiteralAsDouble().Value, 5);
        Assert.Equal("balanced", finder.Performance.LiteralAsString());
        Assert.Same(finder, crop.Finder.Connection?.Node);
        Assert.Equal("person", crop.Subject.LiteralAsString());
        Assert.Equal("face", crop.Part.LiteralAsString());
        Assert.Equal(512, crop.CropWidth.LiteralAsInt());
        Assert.Equal(512, crop.CropHeight.LiteralAsInt());
        Assert.Equal(2.5, crop.SurroundingArea.LiteralAsDouble().Value, 5);
        Assert.Equal(10, crop.TrackedAreaBlur.LiteralAsInt());
        Assert.Equal("off", crop.PerformanceReport.LiteralAsString());
        Assert.Equal(1.0, finish.SmallDetailStrength.LiteralAsDouble().Value, 5);
        Assert.Equal(0.65, finish.MediumDetailStrength.LiteralAsDouble().Value, 5);
        Assert.Equal(0.15, finish.CloseupStrength.LiteralAsDouble().Value, 5);
        Assert.Equal(5, finish.StrengthSmoothingFrames.LiteralAsInt());
        Assert.Equal(0, finish.MaskExpandPixels.LiteralAsInt());
        Assert.Equal(0, finish.MaskFeatherPixels.LiteralAsInt());
        Assert.Equal(0.5, finish.ColorMatchStrength.LiteralAsDouble().Value, 5);
        Assert.Equal("off", finish.PerformanceReport.LiteralAsString());
        Assert.Null(finish.RepairMasks.Connection);
        Assert.True(ReachesUpstream(bridge, sampler, crop.Id));
        Assert.True(ReachesUpstream(bridge, finish, sampler.Id));
        Assert.Equal(
            new JArray(crop.Id, 2),
            workflow[finish.Id]?["inputs"]?["placement_data"]);
        Assert.Equal(4, sampler.Steps.LiteralAsInt());
        Assert.Equal(2, sampler.StartAtStep.LiteralAsInt());
        Assert.DoesNotContain(workflow.Properties(), property =>
            property.Value["class_type"]?.Value<string>() is
                "SAM3_VideoTrack" or "SAM3_Detect" or "MaskComposite" or
                "ImageCompositeMasked" or "SwarmImageCompositeMaskedColorCorrecting");
        Assert.DoesNotContain(
            workflow.Properties(),
            property => property.Value["inputs"]?["noise_mask"] is not null);
        string[] characterNodeClasses =
        [
            BonitaSAM3FinderNode.ClassType,
            BonitaFindAndCropNode.ClassType,
            BonitaApplyRepairNode.ClassType,
        ];
        Assert.Equal(3, workflow.Properties().Count(property =>
            characterNodeClasses.Contains(
                property.Value["class_type"]?.Value<string>())));

        SwarmMiniMaxH3CollectReferencesNode reference = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3CollectReferencesNode>());
        SwarmClipTextEncodeAdvancedNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>());
        Assert.Equal("refine this character", conditioning.Prompt.LiteralAsString());
        Assert.Equal(2, reference.RefImages.Items.Count);
        Assert.Same(crop.CharacterReference.Connection?.Node, reference.RefImages.Items[0].Connection?.Node);
        Assert.Equal(
            crop.CharacterReference.Connection?.SlotIndex,
            reference.RefImages.Items[0].Connection?.SlotIndex);
        Assert.Equal(512, reference.Width.LiteralAsInt());
        Assert.Equal(512, reference.Height.LiteralAsInt());
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public async Task Generic_fine_target_masks_within_the_selected_coarse_track()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        JObject characterRefine = CharacterRefine();
        characterRefine["subject"] = "person";
        characterRefine["part"] = "hand";
        stage["characterRefine"] = characterRefine;

        (JObject workflow, _) = await Generate(fixture, InitVideoClip(stage));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
        BonitaSAM3FinderNode finder = Assert.Single(
            bridge.Graph.NodesOfType<BonitaSAM3FinderNode>());

        Assert.Equal("person", crop.Subject.LiteralAsString());
        Assert.Equal("hand", crop.Part.LiteralAsString());
        Assert.Equal(0.25, finder.SubjectConfidence.LiteralAsDouble().Value, 5);
        Assert.Equal(0.1, finder.PartConfidence.LiteralAsDouble().Value, 5);
        Assert.DoesNotContain(workflow.Properties(), property =>
            property.Value["class_type"]?.Value<string>() is
                "SAM3_VideoTrack" or "SAM3_Detect" or "MaskComposite");
    }

    [Fact]
    public async Task Character_refine_preflight_detects_a_partial_bonita_install()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        using CharacterModelFiles models = CharacterModelFiles.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        JObject characterRefine = CharacterRefine();
        characterRefine["subject"] = "person";
        characterRefine["part"] = "hand";
        stage["characterRefine"] = characterRefine;

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                Post(fixture, InitVideoClip(stage)),
                extraFeatures: Features().Skip(1)));

        Assert.Contains(BonitaSAM3FinderNode.ClassType, error.Message);
        Assert.DoesNotContain(BonitaFindAndCropNode.ClassType, error.Message);
        Assert.DoesNotContain(BonitaApplyRepairNode.ClassType, error.Message);
        Assert.DoesNotContain(BonitaYOLOFinderNode.ClassType, error.Message);
    }

    [Fact]
    public async Task Character_refine_preserves_the_init_video_frame_count()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = CharacterRefine();

        (JObject workflow, WorkflowGenerator generator) = await Generate(
            fixture,
            InitVideoClip(stage));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode[] windows =
            [.. bridge.Graph.NodesOfType<SwarmFrameWindowNode>()];
        SwarmFrameWindowNode source = Assert.Single(windows, node =>
            node.ImagesInput.Connection?.Node is SwarmVideoResampleFPSNode);
        Assert.Equal(25, source.FrameCount.LiteralAsInt());
        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());
        SwarmFrameWindowNode pad = Assert.Single(windows, node =>
            ReferenceEquals(node.ImagesInput.Connection?.Node, crop));
        Assert.Equal(39, pad.FrameCount.LiteralAsInt());
        SwarmFrameWindowNode trim = Assert.Single(windows, node =>
            node.ImagesInput.Connection?.Node is VAEDecodeTiledNode);
        Assert.Equal(25, trim.FrameCount.LiteralAsInt());
        BonitaApplyRepairNode finish = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        Assert.Same(trim, finish.RepairedCrops.Connection?.Node);
        SwarmMiniMaxH3CollectReferencesNode reference = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3CollectReferencesNode>());
        Assert.Equal(39, reference.Length.LiteralAsInt());
        TrimAudioDurationNode audio = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(25 / 24.0, audio.Duration.LiteralAsDouble().Value, 6);
        Assert.Equal(25, generator.CurrentMedia.Frames);
    }

    [Fact]
    public async Task Character_refine_defaults_to_coarse_person_and_fine_face()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = CharacterRefine();

        (JObject workflow, _) = await Generate(fixture, InitVideoClip(stage));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());

        Assert.Equal("person", crop.Subject.LiteralAsString());
        Assert.Equal("face", crop.Part.LiteralAsString());
    }

    [Fact]
    public async Task Identity_is_first_and_optional_references_append_once()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = CharacterRefine(
            "identity-ref",
            "context-ref",
            "identity-ref",
            "context-ref");

        (JObject workflow, _) = await Generate(fixture, InitVideoClip(stage));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmMiniMaxH3CollectReferencesNode reference = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3CollectReferencesNode>());
        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());

        Assert.Equal(2, reference.RefImages.Items.Count);
        Assert.Same(crop.CharacterReference.Connection?.Node, reference.RefImages.Items[0].Connection?.Node);
    }

    [Fact]
    public async Task Identity_verification_uses_the_first_image_even_when_only_the_second_is_checked()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = new JObject
        {
            ["pictureReferenceIds"] = new JArray("identity-1"),
            ["verifyIdentity"] = true,
        };
        JObject clip = InitVideoClip(stage);
        clip["references"] = new JArray(Enumerable.Range(0, 2).Select(index =>
            ImageReference($"identity-{index}", $"identity-{index}.png")));
        clip["references"]![1]!["uploadedMedia"]!["data"] =
            "data:image/png;base64,iVBORw0KGgs=";

        (JObject workflow, _) = await Generate(fixture, clip);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmMiniMaxH3CollectReferencesNode reference = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3CollectReferencesNode>());
        BonitaFindAndCropNode crop = Assert.Single(
            bridge.Graph.NodesOfType<BonitaFindAndCropNode>());

        Assert.Single(reference.RefImages.Items);
        Assert.NotEqual(
            (
                crop.CharacterReference.Connection?.Node.Id,
                crop.CharacterReference.Connection?.SlotIndex),
            (
                reference.RefImages.Items[0].Connection?.Node.Id,
                reference.RefImages.Items[0].Connection?.SlotIndex));
    }

    [Fact]
    public async Task Checked_pictures_must_name_usable_image_references()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = CharacterRefine("missing-ref");

        SwarmReadableErrorException error =
            await Assert.ThrowsAsync<SwarmReadableErrorException>(() =>
                Generate(fixture, InitVideoClip(stage)));

        Assert.Contains("pictureReferenceIds", error.Message);
    }

    [Fact]
    public async Task Consecutive_character_passes_chain_and_preserve_one_final_audio_decode()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject first = fixture.Stage(control: 0.5, steps: 4);
        first["characterRefine"] = CharacterRefine();
        JObject second = fixture.Stage(control: 0.5, steps: 4);
        second["characterRefine"] = CharacterRefine();

        (JObject workflow, WorkflowGenerator generator) = await Generate(
            fixture,
            GeneratedClip(fixture.Stage(steps: 4), first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        BonitaApplyRepairNode[] finishes =
            [.. bridge.Graph.NodesOfType<BonitaApplyRepairNode>()];

        Assert.Equal(2, finishes.Length);
        Assert.True(ReachesUpstream(bridge, finishes[1], finishes[0].Id));
        Assert.IsType<VAEDecodeAudioNode>(
            bridge.ResolvePath((JArray)generator.CurrentMedia.AttachedAudio.Path)?.Node);
        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
    }

    [Fact]
    public async Task Character_refine_reuses_the_source_video_sampler_seed()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject sourceStage = fixture.Stage(control: 0.5, steps: 4);
        JObject bonita = fixture.Stage(control: 0.5, steps: 4);
        bonita["characterRefine"] = CharacterRefine();

        (JObject workflow, _) = await Generate(
            fixture,
            GeneratedClip(sourceStage, bonita));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BonitaApplyRepairNode finish = Assert.Single(
            bridge.Graph.NodesOfType<BonitaApplyRepairNode>());
        SwarmKSamplerNode sourceSampler = bridge.Graph.FindNearestUpstream<SwarmKSamplerNode>(
            finish.VideoFrames.Connection?.Node);
        SwarmKSamplerNode bonitaSampler = bridge.Graph.FindNearestUpstream<SwarmKSamplerNode>(
            finish.RepairedCrops.Connection?.Node);

        Assert.NotNull(sourceSampler);
        Assert.NotNull(bonitaSampler);
        Assert.NotSame(sourceSampler, bonitaSampler);
        Assert.Equal(
            sourceSampler.NoiseSeed.LiteralAsLong(),
            bonitaSampler.NoiseSeed.LiteralAsLong());
    }

    [Fact]
    public async Task Character_refine_uses_the_seed_carried_by_a_refine_video_source()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = CharacterRefine();
        JObject clip = InitVideoClip(stage);

        (JObject workflow, _) = await Generate(
            fixture,
            clip,
            post => post["extra_metadata"] = new JObject
            {
                [VideoSamplerSeed.MetadataKey] = "987",
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmKSamplerNode sampler = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(987L, sampler.NoiseSeed.LiteralAsLong());
    }

    [Fact]
    public async Task Character_refine_does_not_validate_an_ignored_uploaded_keyframe()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0.5, steps: 4);
        stage["characterRefine"] = CharacterRefine();
        JObject clip = InitVideoClip(stage);
        clip["keyframes"] = new JArray(
            UploadedReference("@@@"),
            UploadedReference("@@@", frame: 2),
            UploadedReference("@@@", fromEnd: true));

        (JObject workflow, _) = await Generate(fixture, clip);

        Assert.Contains(
            workflow.Properties(),
            property => IsNode(property, BonitaApplyRepairNode.ClassType));
    }

    [Fact]
    public async Task Zero_control_is_a_plain_passthrough_without_character_nodes()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject stage = fixture.Stage(control: 0, steps: 4);
        stage["characterRefine"] = CharacterRefine();
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-zero-{Guid.NewGuid():N}");
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;

            JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
                Post(fixture, InitVideoClip(stage)),
                extraFeatures: Features());

            Assert.DoesNotContain(workflow.Properties(), property =>
                property.Value["class_type"]?.Value<string>() is
                    "BonitaFindAndCrop" or "BonitaApplyRepair" or "SwarmKSampler");
            Assert.False(Directory.Exists(Path.Join(root, "insightface")));
        }
        finally
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = priorRoot;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool IsNode(JProperty property, string classType) =>
        property.Value["class_type"]?.Value<string>() == classType;

    private static IEnumerable<string> Features() =>
        BonitaIntegration.NodeRequirements.Select(item => item.Feature);

    private static async Task<(JObject Workflow, WorkflowGenerator Generator)> Generate(
        MiniMaxWorkflowFixture fixture,
        JObject clip,
        Action<JObject> customize = null)
    {
        using CharacterModelFiles models = CharacterModelFiles.Create();
        JObject post = Post(fixture, clip);
        customize?.Invoke(post);
        return await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
            post,
            extraFeatures: Features());
    }

    private static JObject Post(MiniMaxWorkflowFixture fixture, JObject clip)
    {
        JObject post = fixture.Post(MakeDocument(clip));
        post["automaticvae"] = false;
        post["vae"] = CommonModels.Known["minimax-h3-video-vae"].FileName;
        return post;
    }

    private static JObject InitVideoClip(params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "character-source.mp4",
        };
        clip["references"] = References();
        return clip;
    }

    private static JObject GeneratedClip(params JObject[] stages)
    {
        JObject clip = MakeClip(1.0, stages);
        clip["references"] = References();
        return clip;
    }

    private static JArray References() => new(
        ImageReference("identity-ref", "identity.png"),
        ImageReference("context-ref", "context.png"));

    private static JObject ImageReference(string id, string fileName) => new()
    {
        ["id"] = id,
        ["kind"] = "image",
        ["source"] = "Upload",
        ["uploadedMedia"] = new JObject
        {
            ["data"] = "data:image/png;base64,iVBORw0KGgo=",
            ["fileName"] = fileName,
        },
    };

    private static JObject CharacterRefine(
        string identityReferenceId = "identity-ref",
        params string[] referenceIds) => new()
    {
        ["pictureReferenceIds"] = new JArray(
            new[] { identityReferenceId }.Concat(referenceIds)),
        ["verifyIdentity"] = true,
    };

    private sealed class CharacterModelFiles : IDisposable
    {
        private readonly string _priorRoot;
        private readonly string _root;
        private readonly BuffaloModelManifest _priorManifest;

        private CharacterModelFiles(
            string priorRoot,
            string root,
            BuffaloModelManifest priorManifest)
        {
            _priorRoot = priorRoot;
            _root = root;
            _priorManifest = priorManifest;
        }

        internal static CharacterModelFiles Create()
        {
            string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
            BuffaloModelManifest priorManifest =
                BonitaIntegration.ReplaceBuffaloManifestForTests(
                    BonitaTestModels.Manifest());
            string root = Path.Join(Path.GetTempPath(), $"character-models-{Guid.NewGuid():N}");
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            string checkpoint = Path.Join(
                root,
                "Stable-Diffusion",
                BonitaIntegration.SamCheckpointRelativePath);
            string buffalo = BonitaIntegration.BuffaloModelDirectory();
            foreach (string path in new[]
            {
                checkpoint,
                Path.Join(buffalo, "det_10g.onnx"),
                Path.Join(buffalo, "w600k_r50.onnx"),
                Path.Join(root, "yolov8", BonitaIntegration.YoloSubjectModel),
                Path.Join(root, "yolov8", BonitaIntegration.YoloPartModel),
            })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(
                    path,
                    path.EndsWith(".onnx", StringComparison.Ordinal)
                        ? BonitaTestModels.Onnx
                        : []);
            }
            return new(priorRoot, root, priorManifest);
        }

        public void Dispose()
        {
            BonitaIntegration.ReplaceBuffaloManifestForTests(_priorManifest);
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = _priorRoot;
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
