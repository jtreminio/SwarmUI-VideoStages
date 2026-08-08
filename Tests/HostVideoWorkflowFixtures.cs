namespace VideoStages.Tests;

/// <summary>
/// A checkpoint the extension has no architecture module for, so the host-video fallback drives it
/// through SwarmUI's own stock video graph. The fallback's frame grid is 1, so the requested frame
/// count passes through unsnapped.
/// </summary>
internal sealed class Hunyuan15WorkflowFixture : VideoStagesWorkflowFixture
{
    public const string ModelFixturePath =
        "models/diffusion_models/Hunyuan15-Workflow-Test.safetensors";

    private Hunyuan15WorkflowFixture(bool withBaseModel)
        : base([ModelFixturePath], withBaseModel)
    {
    }

    public static Hunyuan15WorkflowFixture CreateWithBaseModel() => new(withBaseModel: true);

    protected override string ClipVisionFileName => TestModelFactory.Hunyuan15ClipVisionFileName;

    protected override void InstallSupportModels() =>
        TestModelFactory.InstallHunyuan15SupportModels();

    public override int DefaultSteps => 12;

    public override double DefaultCfgScale => 4.5;

    public override int ExpectedGeneratedFrames => RequestedFrames;
}

/// <summary>A second host-video family, for the text-entry shape.</summary>
internal sealed class MochiWorkflowFixture : VideoStagesWorkflowFixture
{
    public const string ModelFixturePath =
        "models/diffusion_models/Mochi-Workflow-Test.safetensors";

    private MochiWorkflowFixture()
        : base([ModelFixturePath], withBaseModel: false)
    {
    }

    public static MochiWorkflowFixture Create() => new();

    protected override void InstallSupportModels() =>
        TestModelFactory.InstallMochiSupportModels();

    public override int DefaultSteps => 12;

    public override double DefaultCfgScale => 4.5;

    public override int ExpectedGeneratedFrames => RequestedFrames;
}
