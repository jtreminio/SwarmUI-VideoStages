using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Generated;

namespace VideoStages.CharacterRefine;

internal sealed record BuffaloArchiveManifest(string FileName, string Url, string Sha256);

internal sealed record BuffaloModelArtifact(long Size, string Sha256);

internal sealed record BuffaloModelManifest(
    BuffaloArchiveManifest Archive,
    Dictionary<string, BuffaloModelArtifact> Models);

internal static class BonitaIntegration
{
    private const string BuffaloManifestResource = "VideoStages.buffalo_l_manifest.json";
    internal const string BonitaFeatureFlag = "bonita";
    internal const string BonitaNodeUrl = "https://github.com/jtreminio/ComfyUI-Bonita";
    internal const double SurroundingArea = 2.5;
    internal const string SamCheckpoint = "sam3.1_multiplex_fp16.safetensors";
    internal const string SamCheckpointRelativePath = "SAM3/" + SamCheckpoint;
    internal const string SamCheckpointUrl =
        "https://huggingface.co/Comfy-Org/sam3.1/resolve/main/checkpoints/" + SamCheckpoint;
    internal const string SamCheckpointHash =
        "9ba99c92703c2e8b4f47de2d34a539bb8e18923049e238b780d70dbe6368eb03";
    internal const string YoloSubjectModel = "yolov8m.pt";
    internal const string YoloSubjectModelUrl =
        "https://github.com/ultralytics/assets/releases/download/v8.4.0/yolov8m.pt";
    internal const string YoloSubjectModelHash =
        "5d4a90cdc7a21786cc59cd19778e9eafff836df9e2da32524737c7ee6efe4fe5";
    internal const string YoloPartModel = "face_yolov8m.pt";
    internal const string YoloPartModelUrl =
        "https://huggingface.co/Bingsu/adetailer/resolve/"
            + "e4d406dee44c6397ee761a7eb7351c5a410e98d2/face_yolov8m.pt";
    internal const string YoloPartModelHash =
        "e3893a92c5c1907136b6cc75404094db767c1e0cfefe1b43e87dad72af2e4c9f";
    internal static string BuffaloArchive => BuffaloManifest.Archive.FileName;
    internal static string BuffaloArchiveUrl => BuffaloManifest.Archive.Url;
    internal static string BuffaloArchiveHash => BuffaloManifest.Archive.Sha256;
    internal static IReadOnlyDictionary<string, BuffaloModelArtifact> BuffaloModelArtifacts =>
        BuffaloManifest.Models;
    private static readonly object BuffaloInstallLock = new();
    private static BuffaloModelManifest BuffaloManifest { get; set; } = LoadBuffaloManifest();

    internal static IReadOnlyList<(string ClassType, string Feature)> NodeRequirements { get; } =
    [
        (BonitaSAM3FinderNode.ClassType, "bonita.sam3-finder"),
        (BonitaYOLOFinderNode.ClassType, "bonita.yolo-finder"),
        (BonitaFindAndCropNode.ClassType, "bonita.find-and-crop"),
        (BonitaApplyRepairNode.ClassType, "bonita.apply-repair"),
    ];

    internal static void RegisterDependencies()
    {
        InstallableFeatures.RegisterInstallableFeature(new(
            "Bonita",
            BonitaFeatureFlag,
            BonitaNodeUrl,
            "jtreminio",
            "This will install ComfyUI-Bonita for Make Them Bonita.\n"
                + "Do you wish to install?"));
        foreach ((string node, string feature) in NodeRequirements)
        {
            ComfyUIBackendExtension.NodeToFeatureMap[node] = feature;
        }
        AddForwardedFolder("insightface");
    }

    internal static void EnsureSamCheckpoint(
        WorkflowGenerator generator,
        Action<string, string, string, string> downloadModel = null)
    {
        RequireBonitaNodes(generator);
        string checkpointRoot = Program.ServerSettings.Paths.SDModelFolder
            .Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
        string directory = Utilities.CombinePathWithAbsolute(
            Program.ServerSettings.Paths.ActualModelRoot,
            Path.Join(checkpointRoot, "SAM3"));
        Directory.CreateDirectory(directory);
        (downloadModel ?? generator.DownloadModel)(
            SamCheckpoint,
            Path.Join(directory, SamCheckpoint),
            SamCheckpointUrl,
            SamCheckpointHash);
    }

    internal static void EnsureYoloModels(
        WorkflowGenerator generator,
        Action<string, string, string, string> downloadModel = null)
    {
        RequireBonitaNodes(generator);
        string directory = Utilities.CombinePathWithAbsolute(
            Program.ServerSettings.Paths.ActualModelRoot,
            "yolov8");
        Directory.CreateDirectory(directory);
        Action<string, string, string, string> download = downloadModel
            ?? generator.DownloadModel;
        void Ensure(string name, string url, string hash)
        {
            string path = Path.Join(directory, name);
            if (!File.Exists(path))
            {
                download(name, path, url, hash);
            }
        }
        Ensure(YoloSubjectModel, YoloSubjectModelUrl, YoloSubjectModelHash);
        Ensure(YoloPartModel, YoloPartModelUrl, YoloPartModelHash);
    }

    internal static string BuffaloModelDirectory() => Utilities.CombinePathWithAbsolute(
        Program.ServerSettings.Paths.ActualModelRoot,
        Path.Join("insightface", "buffalo_l"));

    private static string LegacyBuffaloModelDirectory() => Utilities.CombinePathWithAbsolute(
        Program.ServerSettings.Paths.ActualModelRoot,
        Path.Join("insightface", "models", "buffalo_l"));

    internal static bool HasBuffaloModel() =>
        HasBuffaloModel(BuffaloModelDirectory())
        || HasBuffaloModel(LegacyBuffaloModelDirectory());

    internal static BuffaloModelManifest ReplaceBuffaloManifestForTests(
        BuffaloModelManifest manifest)
    {
        BuffaloModelManifest prior = BuffaloManifest;
        BuffaloManifest = manifest;
        return prior;
    }

    internal static void EnsureBuffaloModel(
        WorkflowGenerator generator,
        Action<string, string, string, string> downloadModel = null)
    {
        RequireBonitaNodes(generator);
        if (HasBuffaloModel())
        {
            return;
        }
        lock (BuffaloInstallLock)
        {
            if (HasBuffaloModel())
            {
                return;
            }
            string target = BuffaloModelDirectory();
            string parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            string staging = Path.Join(parent, $".buffalo_l-{Guid.NewGuid():N}");
            string extracted = Path.Join(staging, "model");
            string archive = Path.Join(staging, BuffaloArchive);
            string backup = Path.Join(parent, $".buffalo_l-backup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extracted);
            bool backedUp = false;
            try
            {
                (downloadModel ?? generator.DownloadModel)(
                    BuffaloArchive,
                    archive,
                    BuffaloArchiveUrl,
                    BuffaloArchiveHash);
                ExtractBuffaloArchive(archive, extracted);
                string[] invalid = BuffaloManifest.Models
                    .Where(model => !Matches(model.Value, Path.Join(extracted, model.Key)))
                    .Select(model => model.Key)
                    .ToArray();
                if (invalid.Length > 0)
                {
                    throw new InvalidDataException(
                        $"buffalo_l archive has missing or invalid files: "
                            + string.Join(", ", invalid));
                }
                if (Directory.Exists(target))
                {
                    Directory.Move(target, backup);
                    backedUp = true;
                }
                Directory.Move(extracted, target);
                if (backedUp)
                {
                    Directory.Delete(backup, recursive: true);
                    backedUp = false;
                }
            }
            catch (Exception error)
            {
                if (backedUp && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                    backedUp = false;
                }
                Logs.Error(
                    $"Make Them Bonita could not install buffalo_l: {error.Message}.");
                throw new SwarmReadableErrorException(
                    "Make Them Bonita could not install InsightFace buffalo_l.");
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
                if (backedUp && Directory.Exists(backup))
                {
                    Directory.Delete(backup, recursive: true);
                }
            }
        }
    }

    internal static string RenderGeneratedTypeScript() => $$"""
        // Generated from BonitaIntegration.cs. Do not edit by hand.

        export const CHARACTER_REFINE_INSTALL_FEATURE = "{{BonitaFeatureFlag}}";
        export const CHARACTER_REFINE_REQUIRED_FEATURES = [
        {{string.Join("\n", NodeRequirements.Select(requirement => $"    \"{requirement.Feature}\","))}}
        ] as const;
        export const CHARACTER_REFINE_REPOSITORY_URL =
            "{{BonitaNodeUrl}}";
        export const CHARACTER_REFINE_FINDER_DEFAULT = "{{CharacterRefineSpec.DefaultFinder}}";
        export const CHARACTER_REFINE_SUBJECT_DEFAULT = "{{CharacterRefineSpec.DefaultSubject}}";
        export const CHARACTER_REFINE_PART_DEFAULT = "{{CharacterRefineSpec.DefaultPart}}";
        export const CHARACTER_REFINE_SUBJECT_CONFIDENCE_DEFAULT = {{CharacterRefineSpec.DefaultSubjectConfidence}};
        export const CHARACTER_REFINE_PART_CONFIDENCE_DEFAULT = {{CharacterRefineSpec.DefaultPartConfidence}};
        export const CHARACTER_REFINE_DETECTION_THRESHOLD_MIN = {{CharacterRefineSpec.MinDetectionThreshold}};
        export const CHARACTER_REFINE_DETECTION_THRESHOLD_MAX = {{CharacterRefineSpec.MaxDetectionThreshold}};
        export const CHARACTER_REFINE_DETECTION_THRESHOLD_STEP = {{CharacterRefineSpec.DetectionThresholdStep}};
        export const CHARACTER_REFINE_CROP_WIDTH_DEFAULT = {{CharacterRefineSpec.DefaultCropWidth}};
        export const CHARACTER_REFINE_CROP_HEIGHT_DEFAULT = {{CharacterRefineSpec.DefaultCropHeight}};
        export const CHARACTER_REFINE_CROP_DIMENSION_MIN = {{CharacterRefineSpec.MinCropDimension}};
        export const CHARACTER_REFINE_CROP_DIMENSION_MAX = {{CharacterRefineSpec.MaxCropDimension}};
        export const CHARACTER_REFINE_CROP_DIMENSION_STEP = {{CharacterRefineSpec.CropDimensionStep}};
        """ + "\n";

    private static void RequireBonitaNodes(WorkflowGenerator generator)
    {
        if (NodeRequirements.Any(requirement =>
                !generator.Features.Contains(requirement.Feature)))
        {
            throw new SwarmReadableErrorException(
                "Install ComfyUI-Bonita before downloading Make Them Bonita models.");
        }
    }

    private static void ExtractBuffaloArchive(string archivePath, string destination)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(normalized)
                || parts.Any(part => part is "." or ".."))
            {
                throw new InvalidDataException("buffalo_l archive contains an unsafe path");
            }
            if (parts.Length > 0 && parts[0] == "buffalo_l")
            {
                parts = parts[1..];
            }
            if (parts.Length == 0 || entry.FullName.EndsWith('/'))
            {
                continue;
            }
            if (parts.Length != 1)
            {
                throw new InvalidDataException("buffalo_l archive contains an unexpected path");
            }
            string output = Path.Join(destination, parts[0]);
            using Stream source = entry.Open();
            using FileStream target = File.Create(output);
            source.CopyTo(target);
        }
    }

    private static bool HasBuffaloModel(string directory) => BuffaloManifest.Models.All(
        model => Matches(model.Value, Path.Join(directory, model.Key)));

    private static bool Matches(BuffaloModelArtifact artifact, string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        using FileStream stream = File.OpenRead(path);
        if (stream.Length != artifact.Size)
        {
            return false;
        }
        return string.Equals(
            Convert.ToHexString(SHA256.HashData(stream)),
            artifact.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static BuffaloModelManifest LoadBuffaloManifest()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(BuffaloManifestResource)
            ?? throw new InvalidDataException(
                $"Missing embedded model manifest {BuffaloManifestResource}");
        return JsonSerializer.Deserialize<BuffaloModelManifest>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid buffalo_l model manifest");
    }

    private static void AddForwardedFolder(string folder)
    {
        if (!ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Contains(folder))
        {
            ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Add(folder);
        }
    }

}
