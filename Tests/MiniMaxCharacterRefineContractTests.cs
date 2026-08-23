using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.MiniMax;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxCharacterRefineContractTests
{
    private static readonly byte[] TestOnnx =
        [0x08, 0x01, 0x10, 0x02, 0x18, 0x03, 0x20, 0x04, 0x28];

    [Fact]
    public void Generated_vocabulary_and_managed_dependencies_are_current()
    {
        Assert.Equal(
            MiniMaxCharacterRefineIntegration.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedMiniMaxCharacterRefine.ts"));
        foreach ((string node, string feature) in
            MiniMaxCharacterRefineIntegration.NodeRequirements)
        {
            Assert.Equal(feature, ComfyUIBackendExtension.NodeToFeatureMap[node]);
        }
        Assert.Contains(
            MiniMaxCharacterRefineIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaApplyRepair");
        Assert.Contains(
            MiniMaxCharacterRefineIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaFindAndCrop");
        Assert.Contains(
            MiniMaxCharacterRefineIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaSAM3Finder");
        Assert.Contains(
            MiniMaxCharacterRefineIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaYOLOFinder");
        Assert.Contains("insightface", ComfyUISelfStartBackend.FoldersToForwardInComfyPath);
        Assert.Contains("yolov8", ComfyUISelfStartBackend.FoldersToForwardInComfyPath);
        Assert.Contains("clipseg", ComfyUISelfStartBackend.FoldersToForwardInComfyPath);
        Assert.True(InstallableFeatures.ComfyFeatures.TryGetValue(
            "bonita",
            out InstallableFeatures.ComfyInstallableFeature bonita));
        Assert.Equal(
            "https://github.com/jtreminio/ComfyUI-Bonita",
            bonita.URL);
        Assert.DoesNotContain(
            ComfyUISelfStartBackend.RequiredPythonPackages,
            item => item.Item1 is "cython" or "insightface");
        Assert.Equal(
            new(16_923_827, "5838f7fe053675b1c7a08b633df49e7af5495cee0493c7dcf6697200b85b5b91"),
            MiniMaxCharacterRefineIntegration.BuffaloModelArtifacts["det_10g.onnx"]);
        Assert.Equal(
            new(174_383_860, "4c06341c33c2ca1f86781dab0e829f88ad5b64be9fba56e56bc9ebdefc619e43"),
            MiniMaxCharacterRefineIntegration.BuffaloModelArtifacts["w600k_r50.onnx"]);
    }

    [Fact]
    public void Buffalo_model_uses_the_registered_insightface_category_root()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-model-root-{Guid.NewGuid():N}");
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;

            Assert.Equal(
                Path.Join(root, "insightface", "buffalo_l"),
                MiniMaxCharacterRefineIntegration.BuffaloModelDirectory());
        }
        finally
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = priorRoot;
        }
    }

    [Fact]
    public void Existing_legacy_buffalo_install_is_reused_without_a_second_download()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-legacy-model-{Guid.NewGuid():N}");
        BuffaloModelManifest priorManifest =
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(
                TestBuffaloManifest());
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            string legacy = Path.Join(root, "insightface", "models", "buffalo_l");
            Directory.CreateDirectory(legacy);
            File.WriteAllBytes(Path.Join(legacy, "det_10g.onnx"), TestOnnx);
            File.WriteAllBytes(Path.Join(legacy, "w600k_r50.onnx"), TestOnnx);

            MiniMaxCharacterRefineIntegration.EnsureBuffaloModel(
                BonitaGenerator(),
                (_, _, _, _) => Assert.Fail("A valid legacy install must not redownload."));

            Assert.True(MiniMaxCharacterRefineIntegration.HasBuffaloModel());
            Assert.False(Directory.Exists(
                MiniMaxCharacterRefineIntegration.BuffaloModelDirectory()));
        }
        finally
        {
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(priorManifest);
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = priorRoot;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
    public void Sam_checkpoint_download_is_pinned_to_the_swarm_checkpoint_root()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        List<(string Name, string Path, string Url, string Hash)> downloads = [];
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            MiniMaxCharacterRefineIntegration.EnsureSamCheckpoint(
                BonitaGenerator(),
                (name, path, url, hash) => downloads.Add((name, path, url, hash)));

            (string name, string path, string url, string hash) = Assert.Single(downloads);
            Assert.Equal("sam3.1_multiplex_fp16.safetensors", name);
            Assert.Equal(Path.Join(root, "Stable-Diffusion", "SAM3", name), path);
            Assert.Equal(
                "https://huggingface.co/Comfy-Org/sam3.1/resolve/main/checkpoints/" + name,
                url);
            Assert.Equal(
                "9ba99c92703c2e8b4f47de2d34a539bb8e18923049e238b780d70dbe6368eb03",
                hash);
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

    [Fact]
    public void Yolo_models_download_to_the_registered_model_folder()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-yolo-{Guid.NewGuid():N}");
        List<(string Name, string Path, string Url, string Hash)> downloads = [];
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;

            MiniMaxCharacterRefineIntegration.EnsureYoloModels(
                BonitaGenerator(),
                (name, path, url, hash) => downloads.Add((name, path, url, hash)));

            Assert.Equal(2, downloads.Count);
            Assert.Equal(
                (
                    "yolov8m.pt",
                    Path.Join(root, "yolov8", "yolov8m.pt"),
                    "https://github.com/ultralytics/assets/releases/download/v8.4.0/yolov8m.pt",
                    "5d4a90cdc7a21786cc59cd19778e9eafff836df9e2da32524737c7ee6efe4fe5"),
                downloads[0]);
            Assert.Equal(
                (
                    "face_yolov8m.pt",
                    Path.Join(root, "yolov8", "face_yolov8m.pt"),
                    "https://huggingface.co/Bingsu/adetailer/resolve/"
                        + "e4d406dee44c6397ee761a7eb7351c5a410e98d2/face_yolov8m.pt",
                    "e3893a92c5c1907136b6cc75404094db767c1e0cfefe1b43e87dad72af2e4c9f"),
                downloads[1]);
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

    [Fact]
    public void Bonita_models_are_not_downloaded_without_the_comfy_nodes()
    {
        WorkflowGenerator generator = UnitTestStubs.StubGenerator(new JObject());
        Action<string, string, string, string> download =
            (_, _, _, _) => Assert.Fail("Models must not download without ComfyUI-Bonita.");

        Assert.Throws<SwarmReadableErrorException>(() =>
            MiniMaxCharacterRefineIntegration.EnsureSamCheckpoint(generator, download));
        Assert.Throws<SwarmReadableErrorException>(() =>
            MiniMaxCharacterRefineIntegration.EnsureYoloModels(generator, download));
        Assert.Throws<SwarmReadableErrorException>(() =>
            MiniMaxCharacterRefineIntegration.EnsureBuffaloModel(generator, download));
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
        MiniMaxH3ReferenceToVideoNode reference = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3ReferenceToVideoNode>());

        Assert.Empty(bridge.Graph.NodesOfType<BonitaSAM3FinderNode>());
        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        Assert.Equal(
            MiniMaxCharacterRefineIntegration.YoloSubjectModel,
            finder.SubjectModel.LiteralAsString());
        Assert.Equal(
            MiniMaxCharacterRefineIntegration.YoloPartModel,
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
            MiniMaxCharacterRefineIntegration.SamCheckpointRelativePath,
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

        MiniMaxH3ReferenceToVideoNode reference = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3ReferenceToVideoNode>());
        Assert.Equal("refine this character", reference.Prompt.LiteralAsString());
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
    public async Task Character_refine_preflights_the_current_bonita_nodes()
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
                extraFeatures: Features().Where(feature =>
                    feature != MiniMaxCharacterRefineIntegration.BonitaFeatureFlag)));

        Assert.Contains(BonitaFindAndCropNode.ClassType, error.Message);
        Assert.Contains(BonitaApplyRepairNode.ClassType, error.Message);
        Assert.Contains(BonitaSAM3FinderNode.ClassType, error.Message);
        Assert.Contains(BonitaYOLOFinderNode.ClassType, error.Message);
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
        MiniMaxH3ReferenceToVideoNode reference = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3ReferenceToVideoNode>());
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
        MiniMaxH3ReferenceToVideoNode reference = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3ReferenceToVideoNode>());
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
        MiniMaxH3ReferenceToVideoNode reference = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3ReferenceToVideoNode>());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Buffalo_model_download_is_pinned_and_installed_atomically(bool prefixed)
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        List<(string Name, string Url, string Hash)> downloads = [];
        BuffaloModelManifest priorManifest =
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(
                TestBuffaloManifest());
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            MiniMaxCharacterRefineIntegration.EnsureBuffaloModel(
                BonitaGenerator(),
                (name, path, url, hash) =>
                {
                    downloads.Add((name, url, hash));
                    string prefix = prefixed ? "buffalo_l/" : "";
                    WriteZip(path, prefix + "det_10g.onnx", prefix + "w600k_r50.onnx");
                });

            (string name, string url, string hash) = Assert.Single(downloads);
            Assert.Equal("buffalo_l.zip", name);
            Assert.Equal(MiniMaxCharacterRefineIntegration.BuffaloArchiveUrl, url);
            Assert.Equal(MiniMaxCharacterRefineIntegration.BuffaloArchiveHash, hash);
            Assert.True(MiniMaxCharacterRefineIntegration.HasBuffaloModel());
            Assert.Empty(Directory.EnumerateDirectories(
                Path.Join(root, "insightface"), ".buffalo_l-*"));
        }
        finally
        {
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(priorManifest);
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = priorRoot;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Buffalo_model_install_rejects_archive_traversal()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            SwarmReadableErrorException error = Assert.Throws<SwarmReadableErrorException>(() =>
                MiniMaxCharacterRefineIntegration.EnsureBuffaloModel(
                    BonitaGenerator(),
                    (_, path, _, _) => WriteZip(
                        path,
                        "../escape.onnx",
                        "det_10g.onnx",
                        "w600k_r50.onnx")));

            Assert.Contains("could not install InsightFace buffalo_l", error.Message);
            Assert.False(File.Exists(Path.Join(root, "insightface", "escape.onnx")));
            Assert.False(Directory.Exists(MiniMaxCharacterRefineIntegration.BuffaloModelDirectory()));
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

    [Fact]
    public void Buffalo_model_install_rejects_an_incomplete_archive()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            SwarmReadableErrorException error = Assert.Throws<SwarmReadableErrorException>(() =>
                MiniMaxCharacterRefineIntegration.EnsureBuffaloModel(
                    BonitaGenerator(),
                    (_, path, _, _) => WriteZip(path, "det_10g.onnx")));

            Assert.Contains("could not install InsightFace buffalo_l", error.Message);
            Assert.False(Directory.Exists(MiniMaxCharacterRefineIntegration.BuffaloModelDirectory()));
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

    [Fact]
    public void Buffalo_model_install_repairs_truncated_header_valid_models()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        int downloads = 0;
        BuffaloModelManifest priorManifest =
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(
                TestBuffaloManifest());
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            string target = MiniMaxCharacterRefineIntegration.BuffaloModelDirectory();
            Directory.CreateDirectory(target);
            byte[] truncated = [0x08, 0x09, 0x10, 0x01, 0x18, 0x01, 0x20, 0x01];
            File.WriteAllBytes(Path.Join(target, "det_10g.onnx"), truncated);
            File.WriteAllBytes(Path.Join(target, "w600k_r50.onnx"), truncated);

            MiniMaxCharacterRefineIntegration.EnsureBuffaloModel(
                BonitaGenerator(),
                (_, path, _, _) =>
                {
                    downloads++;
                    WriteZip(path, "det_10g.onnx", "w600k_r50.onnx");
                });

            Assert.Equal(1, downloads);
            Assert.True(MiniMaxCharacterRefineIntegration.HasBuffaloModel());
        }
        finally
        {
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(priorManifest);
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = priorRoot;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Buffalo_model_install_repairs_expected_size_wrong_hash_models()
    {
        string priorRoot = SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        int downloads = 0;
        BuffaloModelManifest priorManifest =
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(
                TestBuffaloManifest());
        try
        {
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            string target = MiniMaxCharacterRefineIntegration.BuffaloModelDirectory();
            Directory.CreateDirectory(target);
            byte[] corrupt = [.. TestOnnx];
            corrupt[^1] ^= 0xff;
            File.WriteAllBytes(Path.Join(target, "det_10g.onnx"), corrupt);
            File.WriteAllBytes(Path.Join(target, "w600k_r50.onnx"), corrupt);

            MiniMaxCharacterRefineIntegration.EnsureBuffaloModel(
                BonitaGenerator(),
                (_, path, _, _) =>
                {
                    downloads++;
                    WriteZip(path, "det_10g.onnx", "w600k_r50.onnx");
                });

            Assert.Equal(1, downloads);
            Assert.True(MiniMaxCharacterRefineIntegration.HasBuffaloModel());
        }
        finally
        {
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(priorManifest);
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = priorRoot;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
        MiniMaxCharacterRefineIntegration.NodeRequirements.Select(item => item.Feature);

    private static WorkflowGenerator BonitaGenerator()
    {
        WorkflowGenerator generator = UnitTestStubs.StubGenerator(new JObject());
        generator.Features.Add("bonita");
        return generator;
    }

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

    private static void WriteZip(string path, params string[] entries)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string entry in entries)
        {
            using Stream stream = archive.CreateEntry(entry).Open();
            stream.Write(TestOnnx);
        }
    }

    private static BuffaloModelManifest TestBuffaloManifest()
    {
        string hash = Convert.ToHexString(SHA256.HashData(TestOnnx)).ToLowerInvariant();
        return new(
            new(
                "buffalo_l.zip",
                "https://huggingface.co/vladmandic/insightface-faceanalysis/resolve/main/buffalo_l.zip",
                "ae9b8642895228b3d2d44f3c3386902aace046f6cc510d04520d848a157b9f92"),
            new(StringComparer.Ordinal)
            {
                ["det_10g.onnx"] = new(TestOnnx.Length, hash),
                ["w600k_r50.onnx"] = new(TestOnnx.Length, hash),
            });
    }

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
                MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(
                    TestBuffaloManifest());
            string root = Path.Join(Path.GetTempPath(), $"character-models-{Guid.NewGuid():N}");
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = root;
            string checkpoint = Path.Join(
                root,
                "Stable-Diffusion",
                MiniMaxCharacterRefineIntegration.SamCheckpointRelativePath);
            string buffalo = MiniMaxCharacterRefineIntegration.BuffaloModelDirectory();
            foreach (string path in new[]
            {
                checkpoint,
                Path.Join(buffalo, "det_10g.onnx"),
                Path.Join(buffalo, "w600k_r50.onnx"),
                Path.Join(root, "yolov8", MiniMaxCharacterRefineIntegration.YoloSubjectModel),
                Path.Join(root, "yolov8", MiniMaxCharacterRefineIntegration.YoloPartModel),
            })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(
                    path,
                    path.EndsWith(".onnx", StringComparison.Ordinal)
                        ? TestOnnx
                        : []);
            }
            return new(priorRoot, root, priorManifest);
        }

        public void Dispose()
        {
            MiniMaxCharacterRefineIntegration.ReplaceBuffaloManifestForTests(_priorManifest);
            SwarmUI.Core.Program.ServerSettings.Paths.ModelRoot = _priorRoot;
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
