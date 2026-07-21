using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.IO;
using System.Net.WebSockets;

namespace VideoStages;

/// <summary>API routes the VideoStages frontend calls outside the generation pipeline.</summary>
public static class VideoStagesApi
{
    public static void Register()
    {
        API.RegisterAPICall(VideoStagesDownloadIcLoraWS, true, Permissions.DownloadModels);
    }

    /// <summary>Downloads a curated IC-LoRA preset's weights into the LoRA folder under
    /// <see cref="Constants.IcLoraAutoModelFolder"/>, keeping the upstream filename — the core
    /// DoModelDownloadWS can't, because its name sanitizer strips dots and would mangle names
    /// like "ltx-2.3-...-ref0.5". Emits the core downloader's message shapes (current_percent /
    /// success / error, including its "already exists" message) so the frontend handler and any
    /// user expectations stay uniform.</summary>
    public static async Task<JObject> VideoStagesDownloadIcLoraWS(Session session, WebSocket ws,
        [API.APIParameter("Preset id from the curated IC-LoRA preset list.")] string presetId)
    {
        async Task fail(string message)
            => await ws.SendJson(new JObject() { ["error"] = message }, API.WebsocketTimeout);
        if (!IcLoraWeights.Urls.TryGetValue($"{presetId}".Trim(), out string url))
        {
            await fail($"Unknown IC-LoRA preset '{presetId}'.");
            return null;
        }
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            await fail("LoRA models are not available.");
            return null;
        }
        string folder = $"{handler.DownloadFolderPath}/{Constants.IcLoraAutoModelFolder}";
        string outPath = $"{folder}/{IcLoraWeights.FileStem(url)}.safetensors";
        try
        {
            if (File.Exists(outPath))
            {
                await fail("Model at that save path already exists.");
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
                headers["Authorization"] = $"Bearer {ModelsAPI.TokenTextLimiter.TrimToMatches(hfApiKey)}";
            }
            Logs.Debug($"VideoStages will download IC-LoRA weights from '{url}' to '{Path.GetFullPath(outPath)}'");
            await Utilities.DownloadFile(url, tempPath, (progress, total, perSec) =>
            {
                ws.SendJson(new JObject()
                {
                    ["current_percent"] = progress / (double)total,
                    ["overall_percent"] = 0.2,
                    ["per_second"] = perSec
                }, API.WebsocketTimeout).Wait();
            }, headers: headers);
            File.Move(tempPath, outPath);
            using (ManyReadOneWriteLock.WriteClaim claim = Program.RefreshLock.LockWrite())
            {
                handler.Refresh();
            }
            await ws.SendJson(new JObject() { ["success"] = true }, API.WebsocketTimeout);
        }
        catch (SwarmReadableErrorException err)
        {
            Logs.Warning($"VideoStages IC-LoRA weights download failed: {err.Message}");
            await fail(err.Message);
        }
        catch (Exception ex)
        {
            Logs.Warning($"VideoStages IC-LoRA weights download failed: {ex.ReadableString()}");
            await fail("Failed to download the IC-LoRA weights due to an internal error.");
        }
        return null;
    }
}
