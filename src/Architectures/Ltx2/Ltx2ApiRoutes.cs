using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Net.WebSockets;

namespace VideoStages.Architectures.Ltx2;

internal static class Ltx2ApiRoutes
{
    internal static void Register() =>
        API.RegisterAPICall(
            VideoStagesDownloadIcLoraWS,
            true,
            Permissions.DownloadModels);

    /// <summary>
    /// Downloads a curated IC-LoRA preset's weights. This route exists only to keep the URL
    /// choice server-side (the client sends a preset id, never a URL); the transfer itself is
    /// core's, so model-refusal policy, cancellation, temp-file handling, refresh, and resave all
    /// stay owned by SwarmUI.
    /// </summary>
    public static async Task<JObject> VideoStagesDownloadIcLoraWS(
        Session session,
        WebSocket ws,
        [API.APIParameter("Preset id from the curated LTX IC-LoRA preset list.")]
        string presetId)
    {
        string cleanPresetId = $"{presetId}".Trim();
        if (!IcLoraWeights.Urls.TryGetValue(cleanPresetId, out string url))
        {
            await ws.SendJson(
                new JObject { ["error"] = $"Unknown IC-LoRA preset '{presetId}'." },
                API.WebsocketTimeout);
            return null;
        }
        return await ModelsAPI.DoModelDownloadWS(
            session,
            ws,
            url,
            "LoRA",
            IcLoraWeights.ModelNameFor(cleanPresetId));
    }
}
