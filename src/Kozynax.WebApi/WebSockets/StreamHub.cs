using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Kozynax.WebApi.Services;

namespace Kozynax.WebApi.WebSockets;

/// <summary>
/// Fans out emulator frames to connected WebSocket clients. Each client has a bounded channel
/// (drop-oldest when full) so a slow consumer cannot stall emulation or exhaust memory.
/// </summary>
public sealed class StreamHub : IDisposable
{
    private readonly EmulatorSession _session;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private bool _disposed;

    public StreamHub(EmulatorSession session)
    {
        _session = session;
        session.Frame += OnFrame;
    }

    private void OnFrame(SessionFrame frame)
    {
        if (_disposed) return;
        var msg = JsonSerializer.Serialize(new FrameMessage("frame", frame.StartTact, frame.Cpu), _json);
        foreach (var client in _clients.Values)
        {
            // Backpressure: drop oldest frames if the per-client queue is full.
            while (!client.Out.Writer.TryWrite(msg))
                _ = client.Out.Reader.TryRead(out _);
        }
    }

    public void AddClient(Client client) => _clients[client.Id] = client;

    public void RemoveClient(Guid id)
    {
        if (_clients.TryRemove(id, out var c))
        {
            c.Out.Writer.Complete();
            c.Cts.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Frame -= OnFrame;
        foreach (var c in _clients.Values)
        {
            c.Out.Writer.Complete();
            c.Cts.Cancel();
        }
        _clients.Clear();
    }
}

/// <summary>One connected WebSocket: its socket, outgoing queue and lifecycle token.</summary>
public sealed class Client
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool WantScreen { get; set; }
    public WebSocket Socket { get; init; } = null!;
    public Channel<string> Out { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    public CancellationTokenSource Cts { get; } = new();

    public void Cancel() => Cts.Cancel();
}

/// <summary>Wire-up for the persistent /ws endpoint.</summary>
public static class StreamHubExtensions
{
    public static IApplicationBuilder MapWebSocketStream(this IApplicationBuilder app)
        => app.Map("/ws", branch => branch.Run(HandleAsync));

    private static async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var cts = new CancellationTokenSource();
        var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var hub = ctx.RequestServices.GetRequiredService<StreamHub>();

        var client = new Client { Socket = socket };
        hub.AddClient(client);

        // Optional initial control frame: { wantScreen: true }
        var initial = await ReadTextAsync(socket, cts.Token);
        if (initial is not null)
        {
            var msg = JsonSerializer.Deserialize<StreamMessage>(initial, StreamJson.Json);
            if (msg?.WantScreen == true) client.WantScreen = true;
        }

        var readTask = ReadLoopAsync(socket, cts.Token);
        var writeTask = WriteLoopAsync(socket, client.Out.Reader, cts.Token);

        try
        {
            await Task.WhenAll(readTask, writeTask);
        }
        finally
        {
            hub.RemoveClient(client.Id);
            cts.Cancel();
        }
    }

    private static async Task ReadLoopAsync(WebSocket socket, CancellationToken token)
    {
        var buf = new byte[1024];
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", token);
                    break;
                }
                // Beyond the initial wantScreen frame, control is handled over HTTP in v1.
            }
        }
        catch { /* connection closed */ }
    }

    private static async Task WriteLoopAsync(WebSocket socket, ChannelReader<string> outChannel, CancellationToken token)
    {
        try
        {
            await foreach (var msg in outChannel.ReadAllAsync(token))
            {
                var payload = System.Text.Encoding.UTF8.GetBytes(msg);
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, token);
            }
        }
        catch { /* connection closed */ }
    }

    private static async Task<string?> ReadTextAsync(WebSocket socket, CancellationToken token)
    {
        var buf = new byte[512];
        try
        {
            var r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), token);
            return (r.MessageType == WebSocketMessageType.Close || r.Count == 0) ? null
                : System.Text.Encoding.UTF8.GetString(buf, 0, r.Count);
        }
        catch
        {
            return null;
        }
    }
}

// ---- Shared JSON options --------------------------------------------------

internal static class StreamJson
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

// ---- Message DTOs ---------------------------------------------------------

public sealed record FrameMessage(
    string Type = "frame",
    int Tact = 0,
    CpuStatus Cpu = null!,
    string? ScreenB64 = null);

public sealed record StreamMessage(bool WantScreen);
