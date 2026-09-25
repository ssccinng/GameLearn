using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

internal sealed class FakeObsServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
    public Task Completion { get; }
    public int Port { get; }
    public FakeObsServer(Func<WebSocket, CancellationToken, Task> handler)
    {
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Completion = ServeAsync(handler);
    }
    private async Task ServeAsync(Func<WebSocket, CancellationToken, Task> handler)
    {
        using var client = await listener.AcceptTcpClientAsync(stop.Token);
        using var stream = client.GetStream(); var header = new List<byte>(); var next = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(next, stop.Token) == 0) throw new IOException("Missing WebSocket handshake");
            header.Add(next[0]);
            if (header.Count > 16384) throw new IOException("Handshake too long");
            if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
        }
        var key = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop.Token);
        using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(10));
        await handler(socket, stop.Token);
    }
    public static async Task SendAsync(WebSocket socket, object message, CancellationToken token, bool fragmented = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (fragmented)
        {
            var middle = bytes.Length / 2;
            await socket.SendAsync(new ArraySegment<byte>(bytes, 0, middle), WebSocketMessageType.Text, false, token);
            await socket.SendAsync(new ArraySegment<byte>(bytes, middle, bytes.Length - middle), WebSocketMessageType.Text, true, token);
        }
        else await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }
    public static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream(); var bytes = new byte[4096];
        while (true)
        {
            var part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token); stream.Write(bytes, 0, part.Count);
            if (part.MessageType == WebSocketMessageType.Close) throw new IOException("Unexpected close");
            if (part.EndOfMessage) break;
        }
        using var json = JsonDocument.Parse(stream.ToArray()); return json.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        try { await Completion; } catch (OperationCanceledException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
        stop.Dispose();
    }
}
