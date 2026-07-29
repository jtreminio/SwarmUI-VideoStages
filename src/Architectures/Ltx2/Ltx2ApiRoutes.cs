using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Net.WebSockets;

namespace VideoStages.Architectures.Ltx2;

/// <summary>The transfer the route delegates to; a seam so tests can drive the route without a
/// real download.</summary>
internal delegate Task<JObject> IcLoraCoreDownload(
    Session session,
    WebSocket socket,
    string url,
    string modelName);

internal static class Ltx2ApiRoutes
{
    /// <summary>Model names this route currently has a transfer in flight for. Core keys its
    /// temporary file by model name and does not lock it, so two transfers of one name would
    /// delete and move each other's file.</summary>
    private static readonly ConcurrentDictionary<string, byte> InFlightModelNames =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Register() =>
        API.RegisterAPICall(
            VideoStagesDownloadIcLoraWS,
            true,
            Permissions.DownloadModels);

    /// <summary>
    /// Downloads a curated IC-LoRA preset's weights. This route exists only to keep the URL
    /// choice server-side (the client sends a preset id, never a URL); the transfer and its file
    /// lifecycle are entirely core's. It refuses a second request for a preset it is already
    /// downloading, which it can only do for its own traffic.
    /// <para>
    /// Known host dependency: <see cref="ModelsAPI.DoModelDownloadWS"/> keys an unlocked
    /// <c>&lt;name&gt;.download.tmp</c> by model name and leaves it behind after a failed or
    /// canceled transfer, until the next attempt for that name deletes it. The extension neither
    /// cleans that file nor coordinates with core's own model-download route: the path is core's
    /// private convention, so making it safe belongs to core or to a shared download service.
    /// </para>
    /// </summary>
    public static Task<JObject> VideoStagesDownloadIcLoraWS(
        Session session,
        WebSocket ws,
        [API.APIParameter("Preset id from the curated LTX IC-LoRA preset list.")]
        string presetId) =>
        DownloadIcLora(session, ws, presetId, CoreDownload);

    internal static async Task<JObject> DownloadIcLora(
        Session session,
        WebSocket ws,
        string presetId,
        IcLoraCoreDownload download)
    {
        string cleanPresetId = $"{presetId}".Trim();
        if (!IcLoraWeights.Urls.TryGetValue(cleanPresetId, out string url))
        {
            await ws.SendJson(
                new JObject { ["error"] = $"Unknown IC-LoRA preset '{presetId}'." },
                API.WebsocketTimeout);
            return null;
        }
        string modelName = IcLoraWeights.ModelNameFor(cleanPresetId);
        // Refusing beats queueing: a queued request would sit outside its own socket's lifecycle
        // for the whole of someone else's transfer.
        if (!InFlightModelNames.TryAdd(modelName, 0))
        {
            await ws.SendJson(
                new JObject
                {
                    ["error"] =
                        $"An IC-LoRA download for '{cleanPresetId}' is already in progress.",
                },
                API.WebsocketTimeout);
            return null;
        }
        try
        {
            return await download(session, ws, url, modelName);
        }
        finally
        {
            InFlightModelNames.TryRemove(modelName, out _);
        }
    }

    private static Task<JObject> CoreDownload(
        Session session,
        WebSocket socket,
        string url,
        string modelName) =>
        ModelsAPI.DoModelDownloadWS(session, socket, url, "LoRA", modelName);
}
