using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using VideoStages.Architectures.Ltx2;
using Xunit;

namespace VideoStages.Tests;

public class Ltx2ApiRoutesTests
{
    [Fact]
    public async Task Unknown_preset_is_refused_locally_without_a_session()
    {
        using RecordingWebSocket socket = new();

        JObject result = await Ltx2ApiRoutes.VideoStagesDownloadIcLoraWS(
            null,
            socket,
            "removed-preset");

        Assert.Null(result);
        JObject error = JObject.Parse(Assert.Single(socket.Messages));
        Assert.Equal(["error"], error.Properties().Select(property => property.Name));
        Assert.Contains("removed-preset", (string)error["error"]);
    }

    [Fact]
    public async Task Core_refusal_cleans_a_stale_partial_file()
    {
        using TemporaryDownloadDirectory directory = new();
        using RecordingWebSocket socket = new();
        string modelName = IcLoraWeights.ModelNameFor("deblur");
        string partialPath = directory.PartialPath(modelName);
        directory.WritePartial(modelName);
        IcLoraModelDownloadService service = new(
            async (_, actualSocket, _, _) =>
            {
                await actualSocket.SendJson(
                    new JObject { ["error"] = "Model not found." },
                    API.WebsocketTimeout);
                return null;
            },
            () => directory.Root);

        JObject result = await service.Download(
            null,
            socket,
            IcLoraWeights.Urls["deblur"],
            modelName);

        Assert.Null(result);
        Assert.False(File.Exists(partialPath));
        Assert.Equal(
            "Model not found.",
            (string)JObject.Parse(Assert.Single(socket.Messages))["error"]);
    }

    [Fact]
    public async Task Cancellation_is_propagated_after_partial_cleanup()
    {
        using TemporaryDownloadDirectory directory = new();
        using RecordingWebSocket socket = new();
        string modelName = IcLoraWeights.ModelNameFor("deblur");
        string partialPath = directory.PartialPath(modelName);
        IcLoraModelDownloadService service = new(
            (_, actualSocket, _, _) =>
            {
                Assert.Same(socket, actualSocket);
                directory.WritePartial(modelName);
                return Task.FromException<JObject>(
                    new TaskCanceledException("download canceled"));
            },
            () => directory.Root);

        TaskCanceledException error = await Assert.ThrowsAsync<TaskCanceledException>(
            () => service.Download(
                null,
                socket,
                IcLoraWeights.Urls["deblur"],
                modelName));

        Assert.Contains("download canceled", error.Message);
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Transfer_failure_is_propagated_after_partial_cleanup()
    {
        using TemporaryDownloadDirectory directory = new();
        using RecordingWebSocket socket = new();
        string modelName = IcLoraWeights.ModelNameFor("deblur");
        string partialPath = directory.PartialPath(modelName);
        IcLoraModelDownloadService service = new(
            (_, _, _, _) =>
            {
                directory.WritePartial(modelName);
                return Task.FromException<JObject>(
                    new InvalidOperationException("transfer failed"));
            },
            () => directory.Root);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.Download(
                null,
                socket,
                IcLoraWeights.Urls["deblur"],
                modelName));

        Assert.Equal("transfer failed", error.Message);
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Successful_route_download_preserves_the_final_file()
    {
        using TemporaryDownloadDirectory directory = new();
        using RecordingWebSocket socket = new();
        string modelName = IcLoraWeights.ModelNameFor("deblur");
        string partialPath = directory.PartialPath(modelName);
        string finalPath = directory.FinalPath(modelName);
        string receivedUrl = null;
        string receivedModelName = null;
        IcLoraModelDownloadService service = new(
            (_, _, url, actualModelName) =>
            {
                receivedUrl = url;
                receivedModelName = actualModelName;
                directory.WritePartial(actualModelName);
                File.Move(
                    directory.PartialPath(actualModelName),
                    directory.FinalPath(actualModelName));
                return Task.FromResult(new JObject { ["success"] = true });
            },
            () => directory.Root);

        JObject result = await Ltx2ApiRoutes.DownloadIcLora(
            null,
            socket,
            " deblur ",
            service);

        Assert.True((bool)result["success"]);
        Assert.Equal(IcLoraWeights.Urls["deblur"], receivedUrl);
        Assert.Equal(modelName, receivedModelName);
        Assert.False(File.Exists(partialPath));
        Assert.True(File.Exists(finalPath));
        Assert.Equal("partial model data", File.ReadAllText(finalPath));
    }

    private sealed class TemporaryDownloadDirectory : IDisposable
    {
        internal string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            $"video-stages-download-test-{Guid.NewGuid():N}");

        internal string PartialPath(string modelName) =>
            Path.Combine(Root, $"{CleanName(modelName)}.download.tmp");

        internal string FinalPath(string modelName) =>
            Path.Combine(Root, $"{CleanName(modelName)}.safetensors");

        internal void WritePartial(string modelName)
        {
            string path = PartialPath(modelName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "partial model data");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string CleanName(string modelName) =>
            Utilities.StrictFilenameClean(modelName.Replace(' ', '_'));
    }

    private sealed class RecordingWebSocket : WebSocket
    {
        public List<string> Messages { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string statusDescription,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string statusDescription,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(
                0,
                WebSocketMessageType.Close,
                true));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Messages.Add(Encoding.UTF8.GetString(
                buffer.Array,
                buffer.Offset,
                buffer.Count));
            return Task.CompletedTask;
        }
    }
}
