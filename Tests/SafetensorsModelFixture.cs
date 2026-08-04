using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using System.Runtime.CompilerServices;

namespace VideoStages.Tests;

/// <summary>
/// Loads checked-in header-only safetensors files into one model handler and lets SwarmUI
/// classify them exactly as it would classify real model files.
/// </summary>
internal sealed class SafetensorsModelFixture : IDisposable
{
    private static readonly object ModelClassLock = new();

    public T2IModelHandler Handler { get; }

    public IReadOnlyList<T2IModel> Models { get; }

    public T2IModel Model => Models.Count == 1
        ? Models[0]
        : throw new InvalidOperationException(
            $"This fixture contains {Models.Count} models; select one from Models.");

    private SafetensorsModelFixture(
        IReadOnlyList<string> relativePaths,
        string modelType,
        string caller)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (relativePaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one safetensors fixture path is required.",
                nameof(relativePaths));
        }

        lock (ModelClassLock)
        {
            if (T2IModelClassSorter.ModelClasses.Count == 0)
            {
                T2IModelClassSorter.Init();
            }
        }

        string[] modelPaths = [.. relativePaths.Select(
            path => TestFixturePaths.ResolveForCaller(path, caller))];
        string[] modelFolders = [.. modelPaths
            .Select(path => Path.GetDirectoryName(path)!)
            .Distinct(StringComparer.Ordinal)];
        Handler = new T2IModelHandler
        {
            ModelType = modelType,
            FolderPaths = modelFolders,
            DownloadFolderPath = modelFolders[0],
        };

        try
        {
            List<T2IModel> models = [];
            foreach (string modelPath in modelPaths)
            {
                if (!File.Exists(modelPath))
                {
                    throw new FileNotFoundException(
                        "Safetensors model fixture was not found.",
                        modelPath);
                }
                string modelFolder = Path.GetDirectoryName(modelPath)!;
                T2IModel model = new(
                    Handler,
                    modelFolder,
                    modelPath,
                    Path.GetFileName(modelPath))
                {
                    Title = Path.GetFileName(modelPath),
                };
                JObject header = T2IModel.GetMetadataHeaderFrom(modelPath);
                model.ModelClass = T2IModelClassSorter.IdentifyClassFor(
                    model,
                    header,
                    Handler.ModelType)
                    ?? throw new InvalidOperationException(
                        $"Fixture model '{modelPath}' did not classify as a SwarmUI model.");
                if (!Handler.Models.TryAdd(model.Name, model))
                {
                    throw new InvalidOperationException(
                        $"Fixture model name '{model.Name}' is duplicated.");
                }
                models.Add(model);
            }
            Models = models;
        }
        catch
        {
            Handler.Shutdown();
            throw;
        }
    }

    public static SafetensorsModelFixture Create(
        string relativePath,
        string modelType = "Stable-Diffusion",
        [CallerFilePath] string caller = "") =>
        new([relativePath], modelType, caller);

    public static SafetensorsModelFixture CreateMany(
        IReadOnlyList<string> relativePaths,
        string modelType = "Stable-Diffusion",
        [CallerFilePath] string caller = "") =>
        new(relativePaths, modelType, caller);

    public void Dispose() => Handler.Shutdown();
}

internal static class TestFixturePaths
{
    public static string Resolve(
        string relativePath,
        [CallerFilePath] string caller = "") =>
        ResolveForCaller(relativePath, caller);

    internal static string ResolveForCaller(string relativePath, string caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string[] relativeParts = relativePath.Split(
            new[] { '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(
            [Path.GetDirectoryName(caller)!, "fixtures", .. relativeParts]);
    }
}
