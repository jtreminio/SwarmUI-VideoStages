using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;

namespace VideoStages.Tests;

/// <summary>
/// The LTX decoded chain <c>LtxPostVideoChain.TryCapture</c> walks back from: a sampler, the
/// split of its joint latent, and the video and audio decodes hanging off that split. Hand-built
/// rather than generated because the capture reads a host graph it did not author.
/// </summary>
internal static class LtxDecodedChainWorkflow
{
    /// <summary>No terminal save node — a caller that needs one adds it, since a downstream
    /// consumer of the decode is exactly what the splice paths retarget.</summary>
    internal static JObject Build() => new()
    {
        ["1"] = new JObject
        {
            ["class_type"] = CheckpointLoaderSimpleNode.ClassType,
            ["inputs"] = new JObject { ["ckpt_name"] = "ltxv2.safetensors" }
        },
        ["2"] = new JObject
        {
            ["class_type"] = LTXVAudioVAELoaderNode.ClassType,
            ["inputs"] = new JObject { ["audio_vae_name"] = "audio.safetensors" }
        },
        ["3"] = new JObject
        {
            ["class_type"] = SwarmKSamplerNode.ClassType,
            ["inputs"] = new JObject
            {
                ["model"] = new JArray("1", 0),
                ["seed"] = 42,
                ["steps"] = 20,
                ["cfg"] = 7.0,
                ["sampler_name"] = "euler",
                ["scheduler"] = "normal",
                ["positive"] = new JArray("99", 0),
                ["negative"] = new JArray("98", 0),
                ["latent_image"] = new JArray("97", 0),
                ["denoise"] = 1.0
            }
        },
        ["4"] = new JObject
        {
            ["class_type"] = LTXVSeparateAVLatentNode.ClassType,
            ["inputs"] = new JObject { ["av_latent"] = new JArray("3", 0) }
        },
        ["5"] = new JObject
        {
            ["class_type"] = VAEDecodeNode.ClassType,
            ["inputs"] = new JObject
            {
                ["samples"] = new JArray("4", 0),
                ["vae"] = new JArray("1", 2)
            }
        },
        ["6"] = new JObject
        {
            ["class_type"] = LTXVAudioVAEDecodeNode.ClassType,
            ["inputs"] = new JObject
            {
                ["samples"] = new JArray("4", 1),
                ["audio_vae"] = new JArray("2", 0)
            }
        }
    };
}
