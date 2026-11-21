using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Xunit;

namespace Kozynax.WebApi.Tests.WebSockets;

/// <summary>
/// Exercises the persistent /ws endpoint end-to-end through the in-process test server.
/// The emulator frame pump needs a running SDL host, so these tests validate the WebSocket
/// handshake and close protocol rather than waiting for live frames.
/// </summary>
public sealed class StreamHubWebSocketTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StreamHubWebSocketTests(WebApplicationFactory<Program> factory)
        => _factory = factory;

    [Fact]
    public async Task Connect_SendsInitialFrame_ClosesHandshakeSucceeds()
    {
        // WebSocketClient does not implement IDisposable in this Mvc.Testing build, so no `using`.
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri("http://localhost/ws");

        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, ws.State);

        // Server expects an optional initial control frame: { wantScreen: true }.
        var wantScreen = JsonSerializer.Serialize(new { wantScreen = true });
        await ws.SendAsync(Encoding.UTF8.GetBytes(wantScreen), WebSocketMessageType.Text, true, CancellationToken.None);

        // Close from our side; the server is expected to echo a NormalClosure "bye".
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);

        var buffer = new byte[256];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
        Assert.Equal("bye", result.CloseStatusDescription);
    }

    [Fact]
    public async Task Connect_WithoutWebsocket_ReturnsBadRequest()
    {
        // /ws only accepts WebSocket upgrades; a plain GET must be rejected.
        // WebApplicationFactory.Client is protected; use the public TestServer client instead.
        var response = await _factory.Server.CreateClient().GetAsync("/ws");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
