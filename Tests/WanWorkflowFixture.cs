using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;

namespace VideoStages.Tests;

internal sealed class WanWorkflowFixture : VideoStagesWorkflowFixture
{
    public const string Wan22I2v14bFixturePath =
        "models/diffusion_models/Wan22I2v14b-Workflow-Test.safetensors";

    public const string Wan22Ti2v5bFixturePath =
        "models/diffusion_models/Wan22Ti2v5b-Workflow-Test.safetensors";

    public const string Wan21I2v14bFixturePath =
        "models/diffusion_models/Wan21I2v14b-Workflow-Test.safetensors";

    public const string Wan21T2v14bFixturePath =
        "models/diffusion_models/Wan21T2v14b-Workflow-Test.safetensors";

    /// <summary>
    /// Byte-identical to its low-noise twin: <c>WanClipPlanCompiler</c> detects the pairing from
    /// the file name, not from the model class.
    /// </summary>
    public const string Wan22I2v14bHighNoiseFixturePath =
        "models/diffusion_models/Wan22I2v14bHighNoise-Workflow-Test.safetensors";

    public const string Wan22I2v14bLowNoiseFixturePath =
        "models/diffusion_models/Wan22I2v14bLowNoise-Workflow-Test.safetensors";

    public const int Steps = 12;

    public const double CfgScale = 4.5;

    /// <summary>25 is already on WAN's 4k+1 grid.</summary>
    public const int GeneratedFrames = 25;

    private WanWorkflowFixture(IReadOnlyList<string> modelFixturePaths, bool withBaseModel)
        : base(modelFixturePaths, withBaseModel)
    {
    }

    public static WanWorkflowFixture Create(
        string modelFixturePath = Wan22I2v14bFixturePath) =>
        new([modelFixturePath], withBaseModel: false);

    public static WanWorkflowFixture CreateWithBaseModel(
        string modelFixturePath = Wan22I2v14bFixturePath) =>
        new([modelFixturePath], withBaseModel: true);

    /// <summary>The two-stage high-noise-then-low-noise shape, which needs both checkpoints
    /// loaded at once.</summary>
    public static WanWorkflowFixture CreateNoisePair(bool withBaseModel = false) =>
        new([Wan22I2v14bHighNoiseFixturePath, Wan22I2v14bLowNoiseFixturePath], withBaseModel);

    public T2IModel LowNoiseModel => Models[1];

    /// <summary>
    /// Naming the CLIP-vision model keeps core's <c>RequireVisionModel</c> away from its 1.2 GB
    /// download — see <see cref="TestModelFactory.InstallWanSupportModels"/>.
    /// </summary>
    public override JObject Post(JObject document, Action<JObject> customize = null) =>
        base.Post(document, post =>
        {
            post["clipvisionmodel"] = TestModelFactory.WanClipVisionFileName;
            customize?.Invoke(post);
        });

    protected override void InstallSupportModels() =>
        TestModelFactory.InstallWanSupportModels();

    public override int DefaultSteps => Steps;

    public override double DefaultCfgScale => CfgScale;

    public override int ExpectedGeneratedFrames => GeneratedFrames;
}
