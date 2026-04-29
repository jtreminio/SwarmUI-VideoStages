using Newtonsoft.Json.Linq;
using VideoStages.LTX2;
using Xunit;

namespace VideoStages.Tests;

public class LtxPostVideoChainTryFindAudioDecodeTests
{
    [Fact]
    public void TryFindAudioDecode_skips_decode_with_missing_audio_vae_then_finds_valid()
    {
        JObject workflow = new();
        workflow["bad_decode"] = new JObject
        {
            ["class_type"] = LtxNodeTypes.LTXVAudioVAEDecode,
            ["inputs"] = new JObject
            {
                ["samples"] = new JArray("separate1", 1)
            }
        };
        workflow["good_decode"] = new JObject
        {
            ["class_type"] = LtxNodeTypes.LTXVAudioVAEDecode,
            ["inputs"] = new JObject
            {
                ["samples"] = new JArray("separate1", 1),
                ["audio_vae"] = new JArray("vae_node", 0)
            }
        };

        bool found = LtxPostVideoChain.TryFindAudioDecode(
            workflow,
            "separate1",
            out string audioDecodeId,
            out JArray audioVaeRef);

        Assert.True(found);
        Assert.Equal("good_decode", audioDecodeId);
        Assert.NotNull(audioVaeRef);
        Assert.Equal("vae_node", $"{audioVaeRef[0]}");
        Assert.Equal(0, audioVaeRef[1].Value<int>());
    }

    [Fact]
    public void TryFindAudioDecode_returns_false_when_only_decode_lacks_audio_vae()
    {
        JObject workflow = new();
        workflow["bad_decode"] = new JObject
        {
            ["class_type"] = LtxNodeTypes.LTXVAudioVAEDecode,
            ["inputs"] = new JObject
            {
                ["samples"] = new JArray("separate1", 1)
            }
        };

        bool found = LtxPostVideoChain.TryFindAudioDecode(
            workflow,
            "separate1",
            out string audioDecodeId,
            out JArray audioVaeRef);

        Assert.False(found);
        Assert.Null(audioDecodeId);
        Assert.Null(audioVaeRef);
    }
}
