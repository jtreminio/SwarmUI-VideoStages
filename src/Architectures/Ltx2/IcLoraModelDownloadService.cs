using System.IO;
using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace VideoStages.Architectures.Ltx2;

internal interface IIcLoraModelDownloadService
{
    Task<JObject> Download(
        Session session,
        WebSocket socket,
        string url,
        string modelName);
}

internal delegate Task<JObject> IcLoraCoreDownload(
    Session session,
    WebSocket socket,
    string url,
    string modelName);

/// <summary>
/// Keeps curated IC-LoRA downloads on SwarmUI's transfer path while closing the core downloader's
/// incomplete-file lifecycle gap. Downloads targeting the same file are serialized, and the
/// deterministic temporary file is removed after every terminal outcome.
/// </summary>
internal sealed class IcLoraModelDownloadService(
    IcLoraCoreDownload coreDownload,
    Func<string> loraDownloadFolder) : IIcLoraModelDownloadService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IIcLoraModelDownloadService Production { get; } =
        new IcLoraModelDownloadService(
            (session, socket, url, modelName) => ModelsAPI.DoModelDownloadWS(
                session,
                socket,
                url,
                "LoRA",
                modelName),
            () => Program.T2IModelSets.GetValueOrDefault("LoRA")?.DownloadFolderPath);

    public async Task<JObject> Download(
        Session session,
        WebSocket socket,
        string url,
        string modelName)
    {
        string temporaryPath = ResolveTemporaryPath(modelName);
        if (temporaryPath is null)
        {
            return await coreDownload(session, socket, url, modelName);
        }

        SemaphoreSlim targetLock = _targetLocks.GetOrAdd(
            Path.GetFullPath(temporaryPath),
            _ => new SemaphoreSlim(1, 1));
        await targetLock.WaitAsync();
        try
        {
            return await coreDownload(session, socket, url, modelName);
        }
        finally
        {
            DeletePartialFile(temporaryPath);
            targetLock.Release();
        }
    }

    private string ResolveTemporaryPath(string modelName)
    {
        string folder = loraDownloadFolder();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }
        string cleanName = Utilities.StrictFilenameClean(modelName.Replace(' ', '_'));
        return Path.Combine(folder, $"{cleanName}.download.tmp");
    }

    private static void DeletePartialFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning(
                "VideoStages: failed to clean incomplete IC-LoRA download "
                + $"'{temporaryPath}': {ex.ReadableString()}");
        }
    }
}
