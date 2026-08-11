namespace VideoStages.Tests;

internal sealed class Ltx2WorkflowFixture : VideoStagesWorkflowFixture
{
    public const string ModelFixturePath =
        "models/diffusion_models/Ltx2-Workflow-Test.safetensors";

    /// <summary>
    /// An unsupported base LTX-2 checkpoint. It stays in <c>checkpoints</c> so core loads its
    /// bundled VAE instead of taking the UNET-only diffusion-model path.
    /// </summary>
    public const string UnsupportedModelFixturePath =
        "models/checkpoints/Ltx2NonV23-Workflow-Test.safetensors";

    public const int Steps = 12;

    public const double CfgScale = 4.5;

    /// <summary>25 is already on LTX-2's 8k+1 grid.</summary>
    public const int GeneratedFrames = 25;

    /// <summary>4.0s at 24 fps aligns up to 97 frames, which is 13 LTX latent frames.</summary>
    public const int RetakeClipFrames = 97;

    private Ltx2WorkflowFixture(string modelFixturePath, bool withBaseModel)
        : base([modelFixturePath], withBaseModel)
    {
    }

    public static Ltx2WorkflowFixture Create() =>
        new(ModelFixturePath, withBaseModel: false);

    public static Ltx2WorkflowFixture CreateWithBaseModel() =>
        new(ModelFixturePath, withBaseModel: true);

    public static Ltx2WorkflowFixture CreateUnsupported() =>
        new(UnsupportedModelFixturePath, withBaseModel: false);

    protected override void InstallSupportModels() =>
        TestModelFactory.InstallLtx2SupportModels();

    public override int DefaultSteps => Steps;

    public override double DefaultCfgScale => CfgScale;

    public override int ExpectedGeneratedFrames => GeneratedFrames;
}
