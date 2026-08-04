using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace VideoStages.Tests;

/// <summary>
/// The single source of fixture data for MiniMax workflow contract tests.
/// <para>
/// The checkpoint is a checked-in header-only safetensors classified by SwarmUI's real
/// <see cref="T2IModelClassSorter"/>. Hand-faking the class makes "this model resolves to MiniMax"
/// circular, and <see cref="TestModelFactory"/>'s fake declares 1344x768 where core's registered
/// minimax-h3 class declares 960x960.
/// </para>
/// </summary>
internal sealed class MiniMaxWorkflowFixture : IDisposable
{
    public const string ModelFixturePath =
        "models/diffusion_models/MiniMaxH3-Workflow-Test.safetensors";

    /// <summary>
    /// A plain image checkpoint, for the image-to-video shape where the host generates a base
    /// image and MiniMax drives the video off it. It sits outside <c>diffusion_models</c> on
    /// purpose: <c>T2IModel.IsDiffusionModelsFormat</c> routes anything in that folder to a
    /// UNET-only loader, and the SDXL path then has no text encoder.
    /// </summary>
    public const string BaseModelFixturePath =
        "models/checkpoints/SDXL-Workflow-Test.safetensors";

    public const int Width = 512;
    public const int Height = 512;
    public const int Fps = 24;
    public const long Seed = 1;
    public const int Steps = 8;

    /// <summary>MiniMax H3 is a guidance-distilled model; production runs it at cfg 1.</summary>
    public const double CfgScale = 1;

    /// <summary>Snaps up to 39 on H3's 17k+5 grid.</summary>
    public const int RequestedFrames = 25;

    private readonly SwarmUiTestContext _context;
    private readonly SafetensorsModelFixture _models;

    private MiniMaxWorkflowFixture(bool withBaseModel)
    {
        // clearModelGenSteps: false keeps core's model-gen steps (LoRA loading, VAE override,
        // clip device) in play — a full workflow contract test must exercise them.
        _context = new SwarmUiTestContext(clearModelGenSteps: false);
        try
        {
            TestModelFactory.InstallMiniMaxSupportModels();
            _models = SafetensorsModelFixture.CreateMany(
                withBaseModel ? [ModelFixturePath, BaseModelFixturePath] : [ModelFixturePath]);
            Program.T2IModelSets["Stable-Diffusion"] = _models.Handler;
        }
        catch
        {
            _models?.Dispose();
            _context.Dispose();
            throw;
        }
    }

    public static MiniMaxWorkflowFixture Create() => new(withBaseModel: false);

    public MiniMaxWorkflowFixture InstallModel(string modelType, string fileName)
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

    public static MiniMaxWorkflowFixture CreateWithBaseModel() => new(withBaseModel: true);

    public T2IModel Model => _models.Models[0];

    public T2IModel BaseModel => _models.Models.Count > 1
        ? _models.Models[1]
        : throw new InvalidOperationException(
            "This fixture was built without a base model; use CreateWithBaseModel().");

    public JObject Stage(
        string imageReference = "Generated",
        double control = 1.0,
        double upscale = 1.0,
        string upscaleMethod = "pixel-lanczos",
        int steps = Steps,
        double cfgScale = CfgScale) =>
        Fixtures.MakeStage(
            Model.Name,
            imageReference,
            control,
            upscale,
            upscaleMethod,
            steps,
            cfgScale);

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
        customize?.Invoke(post);
        return post;
    }

    /// <summary>The image-to-video POST shape: the host generates the base image, MiniMax drives
    /// the video from it.</summary>
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

    public void Dispose()
    {
        _models.Dispose();
        _context.Dispose();
    }
}
