using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Tests;

/// <summary>
/// A checkpoint list <see cref="WanWorkflowFixture"/> has no factory for: the Wan timelines that
/// mix profiles or families. Wan's own defaults apply, and both architectures' support models are
/// installed so one fixture serves a cross-architecture timeline — each installer replaces the
/// shared VAE handler, so WAN's VAEs are re-added last.
/// </summary>
internal sealed class MultiModelFixture : VideoStagesWorkflowFixture
{
    private MultiModelFixture(IReadOnlyList<string> modelFixturePaths, bool withBaseModel)
        : base(modelFixturePaths, withBaseModel)
    {
    }

    public static MultiModelFixture Create(params string[] modelFixturePaths) =>
        new(modelFixturePaths, withBaseModel: false);

    public static MultiModelFixture CreateWithBaseModel(params string[] modelFixturePaths) =>
        new(modelFixturePaths, withBaseModel: true);

    public override JObject Post(JObject document, Action<JObject> customize = null) =>
        base.Post(document, post =>
        {
            post["clipvisionmodel"] = TestModelFactory.WanClipVisionFileName;
            customize?.Invoke(post);
        });

    protected override void InstallSupportModels()
    {
        TestModelFactory.InstallWanSupportModels();
        TestModelFactory.InstallLtx2SupportModels();
        InstallModel("VAE", CommonModels.Known["wan21-vae"].FileName);
        InstallModel("VAE", CommonModels.Known["wan22-vae"].FileName);
    }

    public override int DefaultSteps => WanWorkflowFixture.Steps;

    public override double DefaultCfgScale => WanWorkflowFixture.CfgScale;

    public override int ExpectedGeneratedFrames => WanWorkflowFixture.GeneratedFrames;
}
