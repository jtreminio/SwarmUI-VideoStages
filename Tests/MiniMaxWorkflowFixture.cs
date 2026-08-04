namespace VideoStages.Tests;

internal sealed class MiniMaxWorkflowFixture : VideoStagesWorkflowFixture
{
    public const string ModelFixturePath =
        "models/diffusion_models/MiniMaxH3-Workflow-Test.safetensors";

    public const int Steps = 8;

    /// <summary>MiniMax H3 is a guidance-distilled model; production runs it at cfg 1.</summary>
    public const double CfgScale = 1;

    /// <summary>25 requested frames snap up to 39 on H3's 17k+5 grid.</summary>
    public const int GeneratedFrames = 39;

    private MiniMaxWorkflowFixture(bool withBaseModel)
        : base([ModelFixturePath], withBaseModel)
    {
    }

    public static MiniMaxWorkflowFixture Create() => new(withBaseModel: false);

    public static MiniMaxWorkflowFixture CreateWithBaseModel() => new(withBaseModel: true);

    protected override void InstallSupportModels() =>
        TestModelFactory.InstallMiniMaxSupportModels();

    public override int DefaultSteps => Steps;

    public override double DefaultCfgScale => CfgScale;

    public override int ExpectedGeneratedFrames => GeneratedFrames;
}
