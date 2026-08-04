using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace VideoStages.Tests;

/// <summary>
/// Two real video checkpoints from different families in one model handler, so a timeline can
/// name a different architecture per clip through the real POST path.
/// </summary>
internal abstract class MixedArchitectureFixture(IReadOnlyList<string> modelFixturePaths)
    : VideoStagesWorkflowFixture(modelFixturePaths, withBaseModel: true)
{
    internal T2IModel SecondModel => Models[1];

    /// <summary>
    /// Every family's installer replaces the VAE registry wholesale, so running two in sequence
    /// would leave the first family's VAE unresolvable — and an unresolvable VAE is a
    /// multi-gigabyte download, not a failure.
    /// </summary>
    protected static void InstallAll(params Action[] installers)
    {
        Dictionary<string, T2IModel> vaes = [];
        foreach (Action installer in installers)
        {
            installer();
            foreach (KeyValuePair<string, T2IModel> vae in Program.T2IModelSets["VAE"].Models)
            {
                vaes[vae.Key] = vae.Value;
            }
        }
        foreach (KeyValuePair<string, T2IModel> vae in vaes)
        {
            Program.T2IModelSets["VAE"].Models[vae.Key] = vae.Value;
        }
    }

    public override int DefaultSteps => Ltx2WorkflowFixture.Steps;

    public override double DefaultCfgScale => Ltx2WorkflowFixture.CfgScale;

    public override int ExpectedGeneratedFrames => Ltx2WorkflowFixture.GeneratedFrames;
}

internal sealed class LtxAndWanFixture() : MixedArchitectureFixture(
    [Ltx2WorkflowFixture.ModelFixturePath, WanWorkflowFixture.Wan22I2v14bFixturePath])
{
    protected override void InstallSupportModels() => InstallAll(
        TestModelFactory.InstallLtx2SupportModels,
        () => TestModelFactory.InstallWanSupportModels());

    /// <summary>Naming the CLIP-vision model keeps core's <c>RequireVisionModel</c> away from
    /// its 1.2 GB download — see <see cref="TestModelFactory.InstallWanSupportModels"/>.</summary>
    public override JObject Post(JObject document, Action<JObject> customize = null) =>
        base.Post(document, post =>
        {
            post["clipvisionmodel"] = TestModelFactory.WanClipVisionFileName;
            customize?.Invoke(post);
        });
}

internal sealed class LtxAndMiniMaxFixture() : MixedArchitectureFixture(
    [Ltx2WorkflowFixture.ModelFixturePath, MiniMaxWorkflowFixture.ModelFixturePath])
{
    protected override void InstallSupportModels() => InstallAll(
        TestModelFactory.InstallLtx2SupportModels,
        TestModelFactory.InstallMiniMaxSupportModels);
}

/// <summary>A stock-host family beside MiniMax: only the stock-host side claims the host root, so
/// this is the shape where one architecture's claim can collide with another's capture.</summary>
internal sealed class WanAndMiniMaxFixture() : MixedArchitectureFixture(
    [WanWorkflowFixture.Wan22I2v14bFixturePath, MiniMaxWorkflowFixture.ModelFixturePath])
{
    protected override void InstallSupportModels() => InstallAll(
        () => TestModelFactory.InstallWanSupportModels(),
        TestModelFactory.InstallMiniMaxSupportModels);

    public override JObject Post(JObject document, Action<JObject> customize = null) =>
        base.Post(document, post =>
        {
            post["clipvisionmodel"] = TestModelFactory.WanClipVisionFileName;
            customize?.Invoke(post);
        });
}
