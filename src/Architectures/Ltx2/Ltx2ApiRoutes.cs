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
    /// core's, while the extension wrapper guarantees failed or canceled transfers do not retain
    /// core's deterministic temporary file.
    /// </summary>
    public static Task<JObject> VideoStagesDownloadIcLoraWS(
        Session session,
        WebSocket ws,
        [API.APIParameter("Preset id from the curated LTX IC-LoRA preset list.")]
        string presetId) =>
        DownloadIcLora(
            session,
            ws,
            presetId,
            IcLoraModelDownloadService.Production);

    internal static async Task<JObject> DownloadIcLora(
        Session session,
        WebSocket ws,
        string presetId,
        IIcLoraModelDownloadService downloader)
    {
        string cleanPresetId = $"{presetId}".Trim();
        if (!IcLoraWeights.Urls.TryGetValue(cleanPresetId, out string url))
        {
            await ws.SendJson(
                new JObject { ["error"] = $"Unknown IC-LoRA preset '{presetId}'." },
                API.WebsocketTimeout);
            return null;
        }
        return await downloader.Download(
            session,
            ws,
            url,
            IcLoraWeights.ModelNameFor(cleanPresetId));
    }
}
