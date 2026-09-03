using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Utils;
using VideoStages.CharacterRefine;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class BonitaIntegrationTests
{
    [Fact]
    public void Generated_vocabulary_and_managed_dependencies_are_current()
    {
        Assert.Equal(
            BonitaIntegration.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedCharacterRefine.ts"));
        foreach ((string node, string feature) in BonitaIntegration.NodeRequirements)
        {
            Assert.Equal(feature, ComfyUIBackendExtension.NodeToFeatureMap[node]);
        }
        Assert.Contains(
            BonitaIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaApplyRepair");
        Assert.Contains(
            BonitaIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaFindAndCrop");
        Assert.Contains(
            BonitaIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaSAM3Finder");
        Assert.Contains(
            BonitaIntegration.NodeRequirements,
            requirement => requirement.ClassType == "BonitaYOLOFinder");
        Assert.Contains("insightface", ComfyUISelfStartBackend.FoldersToForwardInComfyPath);
        Assert.Contains("yolov8", ComfyUISelfStartBackend.FoldersToForwardInComfyPath);
        Assert.Contains("clipseg", ComfyUISelfStartBackend.FoldersToForwardInComfyPath);
        Assert.True(InstallableFeatures.ComfyFeatures.TryGetValue(
            "bonita",
            out InstallableFeatures.ComfyInstallableFeature bonita));
        Assert.Equal("https://github.com/jtreminio/ComfyUI-Bonita", bonita.URL);
        Assert.DoesNotContain(
            ComfyUISelfStartBackend.RequiredPythonPackages,
            item => item.Item1 is "cython" or "insightface");
        Assert.Equal(
            new(16_923_827, "5838f7fe053675b1c7a08b633df49e7af5495cee0493c7dcf6697200b85b5b91"),
            BonitaIntegration.BuffaloModelArtifacts["det_10g.onnx"]);
        Assert.Equal(
            new(174_383_860, "4c06341c33c2ca1f86781dab0e829f88ad5b64be9fba56e56bc9ebdefc619e43"),
            BonitaIntegration.BuffaloModelArtifacts["w600k_r50.onnx"]);
    }

    [Fact]
    public void Buffalo_model_uses_the_registered_insightface_category_root()
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-model-root-{Guid.NewGuid():N}");
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            Assert.Equal(
                Path.Join(root, "insightface", "buffalo_l"),
                BonitaIntegration.BuffaloModelDirectory());
        }
        finally
        {
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
        }
    }

    [Fact]
    public void Existing_legacy_buffalo_install_is_reused_without_a_second_download()
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-legacy-model-{Guid.NewGuid():N}");
        BuffaloModelManifest priorManifest = BonitaIntegration.ReplaceBuffaloManifestForTests(
            BonitaTestModels.Manifest());
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            string legacy = Path.Join(root, "insightface", "models", "buffalo_l");
            Directory.CreateDirectory(legacy);
            File.WriteAllBytes(Path.Join(legacy, "det_10g.onnx"), BonitaTestModels.Onnx);
            File.WriteAllBytes(Path.Join(legacy, "w600k_r50.onnx"), BonitaTestModels.Onnx);

            BonitaIntegration.EnsureBuffaloModel(
                BonitaTestModels.Generator(),
                (_, _, _, _) => Assert.Fail("A valid legacy install must not redownload."));

            Assert.True(BonitaIntegration.HasBuffaloModel());
            Assert.False(Directory.Exists(BonitaIntegration.BuffaloModelDirectory()));
        }
        finally
        {
            BonitaIntegration.ReplaceBuffaloManifestForTests(priorManifest);
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            Delete(root);
        }
    }

    [Fact]
    public void Sam_checkpoint_download_is_pinned_to_the_swarm_checkpoint_root()
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        List<(string Name, string Path, string Url, string Hash)> downloads = [];
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            BonitaIntegration.EnsureSamCheckpoint(
                BonitaTestModels.Generator(),
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
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            Delete(root);
        }
    }

    [Fact]
    public void Yolo_models_download_to_the_registered_model_folder()
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-yolo-{Guid.NewGuid():N}");
        List<(string Name, string Path, string Url, string Hash)> downloads = [];
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            BonitaIntegration.EnsureYoloModels(
                BonitaTestModels.Generator(),
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
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            Delete(root);
        }
    }

    [Fact]
    public void Bonita_models_are_not_downloaded_without_the_comfy_nodes()
    {
        WorkflowGenerator generator = UnitTestStubs.StubGenerator(new JObject());
        Action<string, string, string, string> download =
            (_, _, _, _) => Assert.Fail("Models must not download without ComfyUI-Bonita.");

        Assert.Throws<SwarmReadableErrorException>(() =>
            BonitaIntegration.EnsureSamCheckpoint(generator, download));
        Assert.Throws<SwarmReadableErrorException>(() =>
            BonitaIntegration.EnsureYoloModels(generator, download));
        Assert.Throws<SwarmReadableErrorException>(() =>
            BonitaIntegration.EnsureBuffaloModel(generator, download));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Buffalo_model_download_is_pinned_and_installed_atomically(bool prefixed)
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        List<(string Name, string Url, string Hash)> downloads = [];
        BuffaloModelManifest priorManifest = BonitaIntegration.ReplaceBuffaloManifestForTests(
            BonitaTestModels.Manifest());
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            BonitaIntegration.EnsureBuffaloModel(
                BonitaTestModels.Generator(),
                (name, path, url, hash) =>
                {
                    downloads.Add((name, url, hash));
                    string prefix = prefixed ? "buffalo_l/" : "";
                    BonitaTestModels.WriteZip(
                        path,
                        prefix + "det_10g.onnx",
                        prefix + "w600k_r50.onnx");
                });

            (string name, string url, string hash) = Assert.Single(downloads);
            Assert.Equal("buffalo_l.zip", name);
            Assert.Equal(BonitaIntegration.BuffaloArchiveUrl, url);
            Assert.Equal(BonitaIntegration.BuffaloArchiveHash, hash);
            Assert.True(BonitaIntegration.HasBuffaloModel());
            Assert.Empty(Directory.EnumerateDirectories(
                Path.Join(root, "insightface"), ".buffalo_l-*"));
        }
        finally
        {
            BonitaIntegration.ReplaceBuffaloManifestForTests(priorManifest);
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            Delete(root);
        }
    }

    [Fact]
    public void Buffalo_model_install_rejects_archive_traversal()
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            SwarmReadableErrorException error = Assert.Throws<SwarmReadableErrorException>(() =>
                BonitaIntegration.EnsureBuffaloModel(
                    BonitaTestModels.Generator(),
                    (_, path, _, _) => BonitaTestModels.WriteZip(
                        path,
                        "../escape.onnx",
                        "det_10g.onnx",
                        "w600k_r50.onnx")));

            Assert.Contains("could not install InsightFace buffalo_l", error.Message);
            Assert.False(File.Exists(Path.Join(root, "insightface", "escape.onnx")));
            Assert.False(Directory.Exists(BonitaIntegration.BuffaloModelDirectory()));
        }
        finally
        {
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            Delete(root);
        }
    }

    [Fact]
    public void Buffalo_model_install_rejects_an_incomplete_archive()
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            SwarmReadableErrorException error = Assert.Throws<SwarmReadableErrorException>(() =>
                BonitaIntegration.EnsureBuffaloModel(
                    BonitaTestModels.Generator(),
                    (_, path, _, _) => BonitaTestModels.WriteZip(path, "det_10g.onnx")));

            Assert.Contains("could not install InsightFace buffalo_l", error.Message);
            Assert.False(Directory.Exists(BonitaIntegration.BuffaloModelDirectory()));
        }
        finally
        {
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            Delete(root);
        }
    }

    [Fact]
    public void Buffalo_model_install_repairs_truncated_header_valid_models()
    {
        AssertRepairsInvalidModels([0x08, 0x09, 0x10, 0x01, 0x18, 0x01, 0x20, 0x01]);
    }

    [Fact]
    public void Buffalo_model_install_repairs_expected_size_wrong_hash_models()
    {
        byte[] corrupt = [.. BonitaTestModels.Onnx];
        corrupt[^1] ^= 0xff;
        AssertRepairsInvalidModels(corrupt);
    }

    private static void AssertRepairsInvalidModels(byte[] invalid)
    {
        string priorRoot = Program.ServerSettings.Paths.ModelRoot;
        string root = Path.Join(Path.GetTempPath(), $"character-refine-{Guid.NewGuid():N}");
        int downloads = 0;
        BuffaloModelManifest priorManifest = BonitaIntegration.ReplaceBuffaloManifestForTests(
            BonitaTestModels.Manifest());
        try
        {
            Program.ServerSettings.Paths.ModelRoot = root;
            string target = BonitaIntegration.BuffaloModelDirectory();
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Join(target, "det_10g.onnx"), invalid);
            File.WriteAllBytes(Path.Join(target, "w600k_r50.onnx"), invalid);

            BonitaIntegration.EnsureBuffaloModel(
                BonitaTestModels.Generator(),
                (_, path, _, _) =>
                {
                    downloads++;
                    BonitaTestModels.WriteZip(path, "det_10g.onnx", "w600k_r50.onnx");
                });

            Assert.Equal(1, downloads);
            Assert.True(BonitaIntegration.HasBuffaloModel());
        }
        finally
        {
            BonitaIntegration.ReplaceBuffaloManifestForTests(priorManifest);
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            Delete(root);
        }
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
