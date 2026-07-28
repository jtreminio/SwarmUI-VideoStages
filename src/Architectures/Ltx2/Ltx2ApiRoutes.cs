using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.IO;
using System.Net.WebSockets;

namespace VideoStages.Architectures.Ltx2;

internal static class Ltx2ApiRoutes
{
    internal static void Register() =>
        API.RegisterAPICall(
            VideoStagesDownloadIcLoraWS,
            true,
            Permissions.DownloadModels);

    public static async Task<JObject> VideoStagesDownloadIcLoraWS(
        Session session,
        WebSocket ws,
        [API.APIParameter("Preset id from the curated LTX IC-LoRA preset list.")]
        string presetId)
    {
        async Task Fail(string message) =>
            await ws.SendJson(
                new JObject { ["error"] = message },
                API.WebsocketTimeout);

        if (!IcLoraWeights.Urls.TryGetValue($"{presetId}".Trim(), out string url))
        {
            await Fail($"Unknown IC-LoRA preset '{presetId}'.");
            return null;
        }
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            await Fail("LoRA models are not available.");
            return null;
        }

        string folder = $"{handler.DownloadFolderPath}/{IcLoraWeights.AutoModelFolder}";
        string outPath = $"{folder}/{IcLoraWeights.FileStem(url)}.safetensors";
        try
        {
            if (File.Exists(outPath))
            {
                await Fail("Model at that save path already exists.");
                return null;
            }
            Directory.CreateDirectory(folder);
            string tempPath = $"{outPath}.download.tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            Dictionary<string, string> headers = [];
            string hfApiKey = session.User.GetGenericData("huggingface_api", "key");
            if (!string.IsNullOrEmpty(hfApiKey))
            {
                headers["Authorization"] =
                    $"Bearer {ModelsAPI.TokenTextLimiter.TrimToMatches(hfApiKey)}";
            }
            Logs.Debug(
                $"VideoStages will download IC-LoRA weights from '{url}' "
                + $"to '{Path.GetFullPath(outPath)}'");
            await Utilities.DownloadFile(url, tempPath, (progress, total, perSec) =>
            {
                ws.SendJson(new JObject
                {
                    ["current_percent"] = progress / (double)total,
                    ["overall_percent"] = 0.2,
                    ["per_second"] = perSec,
                }, API.WebsocketTimeout).Wait();
            }, headers: headers);
            File.Move(tempPath, outPath);
            using (ManyReadOneWriteLock.WriteClaim claim = Program.RefreshLock.LockWrite())
            {
                handler.Refresh();
            }
            await ws.SendJson(new JObject { ["success"] = true }, API.WebsocketTimeout);
        }
        catch (SwarmReadableErrorException error)
        {
            Logs.Warning($"VideoStages IC-LoRA weights download failed: {error.Message}");
            await Fail(error.Message);
        }
        catch (Exception error)
        {
            Logs.Warning(
                $"VideoStages IC-LoRA weights download failed: {error.ReadableString()}");
            await Fail("Failed to download the IC-LoRA weights due to an internal error.");
        }
        return null;
    }
}
