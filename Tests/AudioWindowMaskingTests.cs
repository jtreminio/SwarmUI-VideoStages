using VideoStages.Architectures.Ltx2;
using Xunit;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Audio_retake_window_seconds_are_deterministic()
    {
        // Frames [24, 48) → latent frames [3, 6): the seconds are snapped to the latent boundary
        // pixels (17 and 41) so the mask-by-time node selects exactly those latent frames.
        LtxAudioWindowMasker.AudioMaskWindow w =
            LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(24, 24, 1.0), fps: 24, clipFrames: 97);
        Assert.Equal(17.0 / 24, w.StartTime, 6);
        Assert.Equal(41.0 / 24, w.EndTime, 6);
        Assert.False(w.IsEmpty);

        // Over-long window is clamped to the clip length (latents [6, 12) of 12 → pixels 41–89).
        LtxAudioWindowMasker.AudioMaskWindow clamped =
            LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(48, 999, 1.0), fps: 24, clipFrames: 96);
        Assert.Equal(41.0 / 24, clamped.StartTime, 6);
        Assert.Equal(89.0 / 24, clamped.EndTime, 6);

        // Zero-length retake => empty (no windowing).
        Assert.True(LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(0, 0, 1.0), 24, 97).IsEmpty);
    }
}
