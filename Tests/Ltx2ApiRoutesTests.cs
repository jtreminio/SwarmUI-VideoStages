using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
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
