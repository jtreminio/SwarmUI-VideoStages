using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// The POST shape decides the topology every Wan contract test reads. An image-to-video request
/// keeps core's base image pass and feeds the timeline from it, while the extension prunes core's
/// own WAN video chain — so the stage sampler is the only video sampler left, and reachability
/// upstream from it proves nothing about stage membership. WAN checkpoints live under
/// <c>diffusion_models</c>, so a stage's model branch ends at a <c>UNETLoader</c>.
/// </summary>
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

    protected override string ClipVisionFileName => TestModelFactory.WanClipVisionFileName;

    protected override void InstallSupportModels() =>
        TestModelFactory.InstallWanSupportModels();

    public override int DefaultSteps => Steps;

    public override double DefaultCfgScale => CfgScale;

    public override int ExpectedGeneratedFrames => GeneratedFrames;

    /// <summary>Frames a 0.6s source clip conforms to on WAN's 4k+1 grid at 24 fps.</summary>
    public const int SourceClipFrames = 17;

    /// <summary>The source window opens one second in.</summary>
    public const int SourceClipStartFrame = 24;

    /// <summary>A 1x1 PNG: <c>ValidateParam</c> rejects IMAGE payloads under 10 base64 characters,
    /// and an undecodable one is rejected later still.</summary>
    public const string EndImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42m"
        + "NkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    public const string EndImagePayload = $"data:image/png;base64,{EndImageBase64}";

    /// <summary>
    /// A clip that refines uploaded footage instead of generating from noise. Stage 0's default
    /// <c>Generated</c> reference is stripped, because a source clip conditions on its own footage.
    /// </summary>
    public static JObject SourceClip(params JObject[] stages) =>
        SourceClip(withFileName: true, stages);

    /// <summary>The file name is optional metadata; the inline payload is what materializes.</summary>
    public static JObject SourceClip(bool withFileName, params JObject[] stages)
    {
        JObject clip = Fixtures.SourceClip(0.6, 1.0, stages);
        if (!withFileName)
        {
            ((JObject)clip["initVideo"]).Remove("fileName");
        }
        return clip;
    }

    public static SwarmFrameWindowNode AssertSourceConformChain(
        WorkflowBridge bridge,
        int expectedFrames = SourceClipFrames) =>
        TypedWorkflowAssertions.AssertSourceConformChain(
            bridge,
            SourceClipStartFrame,
            expectedFrames,
            VideoStagesWorkflowFixture.Width,
            VideoStagesWorkflowFixture.Height);

    /// <summary>The single-frame donor WAN conditions from, and the framing scale behind it.</summary>
    public static ImageScaleNode FirstFrameFraming(ComfyNode startImage)
    {
        ImageFromBatchNode donor = Assert.IsType<ImageFromBatchNode>(startImage);
        Assert.Equal(0, donor.BatchIndex.LiteralAsInt());
        Assert.Equal(1, donor.Length.LiteralAsInt());
        return Assert.IsType<ImageScaleNode>(donor.Image.Connection?.Node);
    }

    public static JObject Lora(string name, double weight, double? textEncoderWeight = null)
    {
        JObject lora = new()
        {
            ["name"] = name,
            ["weight"] = weight,
        };
        if (textEncoderWeight is not null)
        {
            lora["textEncoderWeight"] = textEncoderWeight.Value;
        }
        return lora;
    }
}
