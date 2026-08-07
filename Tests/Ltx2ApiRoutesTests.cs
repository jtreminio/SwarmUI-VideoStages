using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
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
    public async Task Known_preset_hands_core_the_curated_url_and_model_name()
    {
        using RecordingWebSocket socket = new();
        string receivedUrl = null;
        string receivedModelName = null;

        JObject result = await Ltx2ApiRoutes.DownloadIcLora(
            null,
            socket,
            " deblur ",
            (_, actualSocket, url, modelName) =>
            {
                Assert.Same(socket, actualSocket);
                receivedUrl = url;
                receivedModelName = modelName;
                return Task.FromResult(new JObject { ["success"] = true });
            });

        Assert.True((bool)result["success"]);
        Assert.Equal(IcLoraWeights.Weights["deblur"].Url, receivedUrl);
        Assert.Equal(IcLoraWeights.ModelNameFor("deblur"), receivedModelName);
        Assert.Empty(socket.Messages);
    }

    [Fact]
    public async Task Second_request_for_an_in_flight_preset_is_refused_locally()
    {
        using RecordingWebSocket firstSocket = new();
        using RecordingWebSocket secondSocket = new();
        TaskCompletionSource<JObject> firstTransfer = new();
        Task<JObject> first = Ltx2ApiRoutes.DownloadIcLora(
            null,
            firstSocket,
            "deblur",
            (_, _, _, _) => firstTransfer.Task);

        // Core would give both transfers the same unlocked temporary file.
        JObject second = await Ltx2ApiRoutes.DownloadIcLora(
            null,
            secondSocket,
            "deblur",
            (_, _, _, _) => throw new InvalidOperationException(
                "the second request must not reach core"));

        Assert.Null(second);
        Assert.Contains(
            "already in progress",
            (string)JObject.Parse(Assert.Single(secondSocket.Messages))["error"]);
        firstTransfer.SetResult(new JObject { ["success"] = true });
        Assert.True((bool)(await first)["success"]);

        // The name is released once the first transfer ends.
        JObject third = await Ltx2ApiRoutes.DownloadIcLora(
            null,
            secondSocket,
            "deblur",
            (_, _, _, _) => Task.FromResult(new JObject { ["success"] = true }));

        Assert.True((bool)third["success"]);
    }

    [Fact]
    public async Task Core_refusal_reaches_the_caller_as_core_reported_it()
    {
        using RecordingWebSocket socket = new();

        // Core's terminal shape for every refusal, failure and cancellation: an error frame on the
        // socket and a null return. The route adds nothing to it.
        JObject result = await Ltx2ApiRoutes.DownloadIcLora(
            null,
            socket,
            "deblur",
            async (_, actualSocket, _, _) =>
            {
                await actualSocket.SendJson(
                    new JObject { ["error"] = "Download was cancelled." },
                    API.WebsocketTimeout);
                return null;
            });

        Assert.Null(result);
        Assert.Equal(
            "Download was cancelled.",
            (string)JObject.Parse(Assert.Single(socket.Messages))["error"]);
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
