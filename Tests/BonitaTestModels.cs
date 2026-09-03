using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using VideoStages.CharacterRefine;

namespace VideoStages.Tests;

internal static class BonitaTestModels
{
    internal static readonly byte[] Onnx =
        [0x08, 0x01, 0x10, 0x02, 0x18, 0x03, 0x20, 0x04, 0x28];

    internal static WorkflowGenerator Generator()
    {
        WorkflowGenerator generator = UnitTestStubs.StubGenerator(new JObject());
        generator.Features.UnionWith(
            BonitaIntegration.NodeRequirements.Select(item => item.Feature));
        return generator;
    }

    internal static JObject CharacterRefineSettings() => new()
    {
        ["pictureReferenceIds"] = new JArray(),
        ["verifyIdentity"] = false,
        ["finder"] = "yolo",
        ["subject"] = "person",
        ["part"] = "face",
        ["subjectConfidence"] = 0.3,
        ["partConfidence"] = 0.2,
        ["cropWidth"] = 384,
        ["cropHeight"] = 320,
    };

    internal static IEnumerable<string> Features() =>
        BonitaIntegration.NodeRequirements.Select(requirement => requirement.Feature);

    internal static IDisposable InstallYoloModels() => YoloModelFiles.Create();

    internal static void WriteZip(string path, params string[] entries)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string entry in entries)
        {
            using Stream stream = archive.CreateEntry(entry).Open();
            stream.Write(Onnx);
        }
    }

    internal static BuffaloModelManifest Manifest()
    {
        string hash = Convert.ToHexString(SHA256.HashData(Onnx)).ToLowerInvariant();
        return new(
            new(
                "buffalo_l.zip",
                "https://huggingface.co/vladmandic/insightface-faceanalysis/resolve/main/buffalo_l.zip",
                "ae9b8642895228b3d2d44f3c3386902aace046f6cc510d04520d848a157b9f92"),
            new(StringComparer.Ordinal)
            {
                ["det_10g.onnx"] = new(Onnx.Length, hash),
                ["w600k_r50.onnx"] = new(Onnx.Length, hash),
            });
    }

    private sealed class YoloModelFiles : IDisposable
    {
        private readonly string priorRoot;
        private readonly string root;

        private YoloModelFiles(string priorRoot, string root)
        {
            this.priorRoot = priorRoot;
            this.root = root;
        }

        internal static YoloModelFiles Create()
        {
            string priorRoot = Program.ServerSettings.Paths.ModelRoot;
            string root = Path.Join(
                Path.GetTempPath(),
                $"bonita-yolo-models-{Guid.NewGuid():N}");
            Program.ServerSettings.Paths.ModelRoot = root;
            foreach (string fileName in new[]
            {
                "yolov8m.pt",
                "face_yolov8m.pt",
            })
            {
                string path = Path.Join(root, "yolov8", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
            }
            return new(priorRoot, root);
        }

        public void Dispose()
        {
            Program.ServerSettings.Paths.ModelRoot = priorRoot;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
