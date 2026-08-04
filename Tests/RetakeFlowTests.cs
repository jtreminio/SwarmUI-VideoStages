using VideoStages.Architectures.Ltx2;
using Xunit;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    /// <summary>
    /// The latent-window arithmetic on its own, over cases the LTX-2 frame grid cannot present
    /// through a POST: a 16-frame clip and an over-long window that has to be clamped. The graph
    /// this drives lives in <see cref="Ltx2GuideAndRetakeContractTests"/>.
    /// </summary>
    [Fact]
    public void Retake_latent_window_arithmetic_is_deterministic()
    {
        LtxVideoRetakeMasker.LatentWindow w =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames: 97, startFrame: 24, lengthFrames: 24);
        Assert.Equal(3, w.Prefix);
        Assert.Equal(3, w.Window);
        Assert.Equal(7, w.Suffix);
        Assert.Equal(13, w.LatentLength);

        LtxVideoRetakeMasker.LatentWindow full =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames: 16, startFrame: 0, lengthFrames: 16);
        Assert.Equal(0, full.Prefix);
        Assert.Equal(2, full.Window);
        Assert.Equal(0, full.Suffix);

        // Over-long request is clamped to the clip.
        LtxVideoRetakeMasker.LatentWindow clamped =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames: 25, startFrame: 8, lengthFrames: 999);
        Assert.Equal(1, clamped.Prefix);
        Assert.Equal(clamped.LatentLength - 1, clamped.Window);
        Assert.Equal(0, clamped.Suffix);
    }
}
