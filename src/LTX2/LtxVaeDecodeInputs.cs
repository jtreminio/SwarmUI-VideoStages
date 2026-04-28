using Newtonsoft.Json.Linq;

namespace VideoStages.LTX2;

internal static class LtxVaeDecodeInputs
{
    public static JArray TryGetDecodeSamplesRef(JObject decodeInputs)
    {
        if (decodeInputs is null)
        {
            return null;
        }

        return decodeInputs["samples"] as JArray
            ?? decodeInputs["latent"] as JArray
            ?? decodeInputs["latents"] as JArray;
    }
}
