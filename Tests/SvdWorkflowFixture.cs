namespace VideoStages.Tests;

/// <summary>
/// Stable Video Diffusion, which the extension has no architecture module for, so the host-video
/// fallback drives it through SwarmUI's own stock video graph.
/// <para>
/// Two things about the checkpoint are load-bearing. Its folder: core's SVD branch emits
/// <c>ImageOnlyCheckpointLoader</c>, which carries the model, its CLIP-vision and its VAE in one
/// node, so there are no support stubs to install and nothing to download — but only a
/// <c>checkpoints/</c> model gets that loader. And its architecture id: the registered
/// <c>stable-video-diffusion-img2vid-v1</c> class declares <c>IsThisModelOfClass = false</c>, which
/// <c>T2IModelClassSorter.IdentifyClassFor</c> never consults because the header's
/// <c>modelspec.architecture</c> matches the class id outright.
/// </para>
/// <para>
/// The compat class is image-to-video only, so there is no text-to-video shape to test.
/// </para>
/// </summary>
internal sealed class SvdWorkflowFixture : VideoStagesWorkflowFixture
{
    public const string ModelFixturePath = "models/checkpoints/Svd-Workflow-Test.safetensors";

    private SvdWorkflowFixture()
        : base([ModelFixturePath], withBaseModel: true)
    {
    }

    public static SvdWorkflowFixture CreateWithBaseModel() => new();

    protected override void InstallSupportModels()
    {
    }

    public override int DefaultSteps => 12;

    public override double DefaultCfgScale => 4.5;

    /// <summary>The host-video fallback's frame grid is 1, so nothing snaps.</summary>
    public override int ExpectedGeneratedFrames => RequestedFrames;
}
