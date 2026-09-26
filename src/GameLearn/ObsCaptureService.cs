using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace GameLearn;

public sealed class ObsConnectionOptions
{
    public Uri Address { get; }
    public string Password { get; }
    public ObsConnectionOptions(int port, string password)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("OBS 端口须为 1–65535。");
        Address = new Uri($"ws://127.0.0.1:{port}"); Password = password;
    }
    public static ObsConnectionOptions FromSettings(AppSettings settings)
    {
        if (System.Diagnostics.Process.GetProcessesByName("obs64").Any(p => p.MainWindowTitle.Contains("SAFE MODE", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("OBS 处于安全模式，WebSocket 模块未加载。请退出安全模式重新打开 OBS，再在 Tools → WebSocket Server Settings 开启服务。");
        if (!settings.ObsUseLocalConfiguration) return new(settings.ObsPort, AppSettings.Unprotect(settings.ObsSecret));
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugin_config", "obs-websocket", "config.json");
        if (!File.Exists(path)) throw new InvalidOperationException("未找到本机 OBS 配置。请在设置中手动填写 OBS WebSocket 端口和密码。");
        using var json = JsonDocument.Parse(File.ReadAllText(path)); var root = json.RootElement;
        if (!root.TryGetProperty("server_enabled", out var enabled) || !enabled.GetBoolean())
            throw new InvalidOperationException("请先在 OBS 的 Tools → WebSocket Server Settings 中开启 Enable WebSocket server，再点击「连接 OBS」。");
        return new(root.GetProperty("server_port").GetInt32(), root.TryGetProperty("auth_required", out var auth) && auth.GetBoolean()
            ? root.GetProperty("server_password").GetString() ?? "" : "");
    }
}

/// <summary>Read-only obs-websocket v5 client. No desktop pixels, recording commands, or source mutations.</summary>
public sealed class ObsCaptureService(Func<ObsConnectionOptions> configuration) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private ClientWebSocket? socket;
    private ObsConnectionOptions? connectedOptions;
    public bool IsConnected => socket?.State == WebSocketState.Open;
    private const int MaximumMessageBytes = 64 * 1024 * 1024;

    public static string Authentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }
    private async Task ConnectLockedAsync(CancellationToken token)
    {
        var options = configuration();
        if (IsConnected && connectedOptions?.Address == options.Address && connectedOptions.Password == options.Password) return;
        DropConnection(); socket = new ClientWebSocket();
        await socket.ConnectAsync(options.Address, token);
        var hello = await ReceiveAsync(token);
        if (hello.GetProperty("op").GetInt32() != 0) throw new InvalidDataException("OBS 握手格式不正确，需要 obs-websocket 5.x。");
        var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        if (hello.GetProperty("d").TryGetProperty("authentication", out var auth))
        {
            if (string.IsNullOrEmpty(options.Password)) throw new InvalidOperationException("OBS 要求连接密码，请在设置中填写，或启用「读取本机 OBS 配置」。");
            identify["authentication"] = Authentication(options.Password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
        }
        await SendAsync(new { op = 1, d = identify }, token);
        var identified = await ReceiveAsync(token);
        if (identified.GetProperty("op").GetInt32() != 2) throw new InvalidDataException("OBS 身份验证没有完成。");
        connectedOptions = options;
    }
    private async Task<JsonElement> RequestLockedAsync(string type, object? data, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await SendAsync(new { op = 6, d = new { requestType = type, requestId = id, requestData = data ?? new { } } }, token);
        while (true)
        {
            var message = await ReceiveAsync(token);
            if (message.GetProperty("op").GetInt32() != 7) continue;
            var body = message.GetProperty("d");
            if (body.GetProperty("requestId").GetString() != id) continue;
            var status = body.GetProperty("requestStatus");
            if (!status.GetProperty("result").GetBoolean())
                throw new InvalidOperationException($"OBS 读取失败（{status.GetProperty("code").GetInt32()}）。来源可能已删除、改名或不可用，请刷新 OBS 来源。");
            return body.TryGetProperty("responseData", out var response) ? response.Clone() : JsonSerializer.SerializeToElement(new { });
        }
    }
    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await ConnectLockedAsync(deadline.Token); return await operation(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { DropConnection(); throw new TimeoutException("读取 OBS 超时，请确认 OBS 正在运行，且 WebSocket 服务已开启。"); }
        catch (WebSocketException)
        { DropConnection(); throw new InvalidOperationException("无法连接 OBS。请检查 OBS 的 WebSocket 服务、端口和密码，再重新连接。"); }
        catch { DropConnection(); throw; }
        finally { gate.Release(); }
    }
    public Task<IReadOnlyList<WindowSource>> ListSourcesAsync(CancellationToken token) => RunAsync<IReadOnlyList<WindowSource>>(async ct =>
    {
        var sceneList = await RequestLockedAsync("GetSceneList", null, ct);
        var current = sceneList.GetProperty("currentProgramSceneName").GetString();
        var active = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(current))
        {
            var items = await RequestLockedAsync("GetSceneItemList", new { sceneName = current }, ct);
            foreach (var item in items.GetProperty("sceneItems").EnumerateArray())
                if (item.GetProperty("sceneItemEnabled").GetBoolean()) active.Add(item.GetProperty("sourceName").GetString()!);
        }
        var inputs = await RequestLockedAsync("GetInputList", null, ct);
        return BuildSourceList(sceneList, inputs, active);
    }, token);

    public static IReadOnlyList<WindowSource> BuildSourceList(JsonElement scenes, JsonElement inputs, ISet<string> active)
    {
        var list = new List<WindowSource> { new(0, "当前输出画面", "OBS", CaptureSourceKind.ObsProgram) };
        var visual = new[] { "game_capture", "window_capture", "monitor_capture", "dshow_input", "av_capture_input", "ffmpeg_source", "image_source", "browser_source", "vlc_source" };
        var captureInputs = inputs.GetProperty("inputs").EnumerateArray().Select(input => new
        {
            Name = input.GetProperty("inputName").GetString()!,
            Kind = input.TryGetProperty("unversionedInputKind", out var kind) ? kind.GetString()! : input.GetProperty("inputKind").GetString()!
        }).Where(input => visual.Contains(input.Kind)).OrderByDescending(input => active.Contains(input.Name))
          .ThenBy(input => input.Kind is "game_capture" or "dshow_input" or "av_capture_input" ? 0 : 1).ThenBy(input => input.Name);
        list.AddRange(captureInputs.Select(input => new WindowSource(0, input.Name, "OBS", CaptureSourceKind.ObsInput, input.Name, active.Contains(input.Name))));
        foreach (var scene in scenes.GetProperty("scenes").EnumerateArray())
        {
            var name = scene.GetProperty("sceneName").GetString()!;
            list.Add(new(0, name, "OBS", CaptureSourceKind.ObsScene, name));
        }
        return list;
    }

    public Task<CapturedFrame> CaptureAsync(WindowSource source, string game, CropRegion? crop, CancellationToken token, bool retainPixels = false)
        => RunAsync(async ct =>
        {
            var name = source.ObsName;
            if (source.Kind == CaptureSourceKind.ObsProgram)
                name = (await RequestLockedAsync("GetCurrentProgramScene", null, ct)).GetProperty("currentProgramSceneName").GetString();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("请选择一个有效的 OBS 场景或捕获源。");
            var response = await RequestLockedAsync("GetSourceScreenshot", new { sourceName = name, imageFormat = "png" }, ct);
            var bytes = DecodeScreenshot(response.GetProperty("imageData").GetString()!);
            return await Task.Run(() =>
            {
                using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("OBS 返回的图片无法解码。");
                if (CaptureService.IsBlank(bitmap)) throw new InvalidOperationException("OBS 捕获源当前为黑画面，请确认来源有信号且已激活。");
                return CaptureService.CreateFrame(bitmap, game, $"OBS · {name}", crop, retainPixels);
            }, ct);
        }, token);
    public static byte[] DecodeScreenshot(string data)
    {
        const string prefix = "data:image/png;base64,";
        if (!data.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OBS 没有返回 PNG 图片数据。");
        return Convert.FromBase64String(data[prefix.Length..]);
    }
    private async Task SendAsync(object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }
    private async Task<JsonElement> ReceiveAsync(CancellationToken token)
    {
        using var stream = new MemoryStream(); var buffer = new byte[64 * 1024];
        while (true)
        {
            var received = await socket!.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException((int?)received.CloseStatus == 4009 ? "OBS 密码验证失败，请检查连接设置。" : "OBS 已断开连接，请重新连接。");
            if (received.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("OBS 返回了不支持的消息格式。");
            if (stream.Length + received.Count > MaximumMessageBytes) throw new InvalidDataException("OBS 图像消息过大。");
            stream.Write(buffer, 0, received.Count);
            if (received.EndOfMessage) break;
        }
        using var json = JsonDocument.Parse(stream.ToArray()); return json.RootElement.Clone();
    }
    private void DropConnection() { socket?.Abort(); socket?.Dispose(); socket = null; connectedOptions = null; }
    public async Task ResetAsync() { await gate.WaitAsync(); try { DropConnection(); } finally { gate.Release(); } }
    public void Dispose() { DropConnection(); gate.Dispose(); }
}
