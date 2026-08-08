using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace VideoStages.Tests;

/// <summary>
/// Shared fixture data for the generated-workflow contract tests: real checked-in checkpoints
/// classified by SwarmUI's own <see cref="T2IModelClassSorter"/>, the support-model stubs each
/// architecture's loader branch resolves, and the POST bodies the Generate tab sends.
/// <para>
/// Hand-faking a model class makes "this model resolves to architecture X" circular, and
/// <see cref="TestModelFactory"/>'s fakes declare dimensions core's registered classes do not.
/// </para>
/// </summary>
internal abstract class VideoStagesWorkflowFixture : IDisposable
{
    /// <summary>
    /// A plain image checkpoint, for the image-to-video shape where the host generates a base
    /// image and the video model drives from it. It sits outside <c>diffusion_models</c> on
    /// purpose: <c>T2IModel.IsDiffusionModelsFormat</c> routes anything in that folder to a
    /// UNET-only loader, and the SDXL path then has no text encoder.
    /// </summary>
    public const string BaseModelFixturePath =
        "models/checkpoints/SDXL-Workflow-Test.safetensors";

    public const int Width = 512;
    public const int Height = 512;
    public const int Fps = 24;
    public const long Seed = 1;

    /// <summary>What the POST asks for; each architecture snaps it up to its own frame grid.</summary>
    public const int RequestedFrames = 25;

    private readonly SwarmUiTestContext _context;
    private readonly SafetensorsModelFixture _models;
    private readonly bool _withBaseModel;

    protected VideoStagesWorkflowFixture(
        IReadOnlyList<string> modelFixturePaths,
        bool withBaseModel)
    {
        // clearModelGenSteps: false keeps core's model-gen steps (LoRA loading, VAE override,
        // clip device) in play — a full workflow contract test must exercise them.
        _context = new SwarmUiTestContext(clearModelGenSteps: false);
        _withBaseModel = withBaseModel;
        try
        {
            InstallSupportModels();
            _models = SafetensorsModelFixture.CreateMany(
                withBaseModel ? [.. modelFixturePaths, BaseModelFixturePath] : modelFixturePaths);
            Program.T2IModelSets["Stable-Diffusion"] = _models.Handler;
        }
        catch
        {
            _models?.Dispose();
            _context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Installs the clip/VAE stubs the architecture's loader branch resolves. A missing stub does
    /// not fail fast: <c>RequireClipModel</c> and the VAE loaders fall through to a multi-gigabyte
    /// <c>DownloadNow().Wait()</c>, which reads as a hung test.
    /// </summary>
    protected abstract void InstallSupportModels();

    /// <summary>
    /// Named on families whose loader branch reaches core's <c>RequireVisionModel</c>: leaving it
    /// null there is a multi-gigabyte download, not a failure. Null means the family never asks.
    /// </summary>
    protected virtual string ClipVisionFileName => null;

    public abstract int DefaultSteps { get; }

    public abstract double DefaultCfgScale { get; }

    /// <summary>Frames the architecture's grid snaps <see cref="RequestedFrames"/> up to.</summary>
    public abstract int ExpectedGeneratedFrames { get; }

    public T2IModel Model => _models.Models[0];

    /// <summary>Every checkpoint the fixture loaded, in the order it named them.</summary>
    public IReadOnlyList<T2IModel> Models => _models.Models;

    public T2IModel BaseModel => _withBaseModel
        ? _models.Models[^1]
        : throw new InvalidOperationException(
            "This fixture was built without a base model; use its CreateWithBaseModel().");

    public VideoStagesWorkflowFixture InstallModel(string modelType, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!Program.T2IModelSets.TryGetValue(modelType, out T2IModelHandler handler))
        {
            handler = new T2IModelHandler { ModelType = modelType };
            Program.T2IModelSets[modelType] = handler;
        }
        TestStubModel.Install(handler, fileName);
        return this;
    }

    /// <summary>Default steps/cfg dispatch to the architecture, so they cannot be compile-time
    /// constants and cannot be plain default parameter values.</summary>
    public JObject Stage(
        string imageReference = "Generated",
        double control = 1.0,
        double upscale = 1.0,
        string upscaleMethod = "pixel-lanczos",
        int? steps = null,
        double? cfgScale = null) =>
        Fixtures.MakeStage(
            Model.Name,
            imageReference,
            control,
            upscale,
            upscaleMethod,
            steps ?? DefaultSteps,
            cfgScale ?? DefaultCfgScale);

    /// <summary>The POST body the Generate tab sends.</summary>
    public JObject Post(JObject document, Action<JObject> customize = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        JObject post = new()
        {
            ["prompt"] = "A red kite flying over a green field",
            ["seed"] = Seed,
            ["width"] = Width,
            ["height"] = Height,
            ["model"] = Model.Name,
            ["automaticvae"] = true,
            ["text2videoframes"] = RequestedFrames,
            ["videofps"] = Fps,
            ["videostagesenabled"] = true,
            ["videostages"] = document.ToString(),
        };
        if (ClipVisionFileName is not null)
        {
            post["clipvisionmodel"] = ClipVisionFileName;
        }
        customize?.Invoke(post);
        return post;
    }

    /// <summary>The image-to-video POST shape: the host generates the base image, the video model
    /// drives the video from it.</summary>
    public JObject ImageToVideoPost(JObject document, Action<JObject> customize = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        JObject post = Post(document, null);
        post["model"] = BaseModel.Name;
        post["videomodel"] = Model.Name;
        post["videoframes"] = RequestedFrames;
        post.Remove("text2videoframes");
        customize?.Invoke(post);
        return post;
    }

    public Task<JObject> GenerateAsync(JObject document, Action<JObject> customize = null) =>
        ComfyWorkflowApiTestHarness.GenerateAsync(Post(document, customize));

    public Task<JObject> GenerateImageToVideoAsync(
        JObject document,
        Action<JObject> customize = null) =>
        ComfyWorkflowApiTestHarness.GenerateAsync(ImageToVideoPost(document, customize));

    /// <summary>
    /// Stage sampler seeds, flat across clips. Core's own image-to-video sampler also seeds
    /// <c>Seed + 42</c>, so wherever the host root survives, stage 0's seed matches two samplers
    /// and those tests must select by reachability instead.
    /// </summary>
    public static long StageSeed(int stageId) => Seed + 42 + stageId;

    /// <summary>
    /// Core's base pass, which exists only in the image-to-video shape — a text-to-video request
    /// discards the root chain, so core's sampler is swept and the stage sampler is the only one
    /// left. Seeds: core base <see cref="Seed"/>, core refiner <c>Seed + 1</c>, stages
    /// <c>Seed + 42 + StageId</c> flat across clips.
    /// </summary>
    public SwarmKSamplerNode BaseSampler(WorkflowBridge bridge) =>
        TypedWorkflowAssertions.StageSampler(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            Seed);

    public SwarmKSamplerNode RefinerSampler(WorkflowBridge bridge) =>
        TypedWorkflowAssertions.StageSampler(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            Seed + 1);

    public void Dispose()
    {
        _models.Dispose();
        _context.Dispose();
    }
}
