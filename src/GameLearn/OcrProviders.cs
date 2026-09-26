using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;
using System.Security.Cryptography;

namespace GameLearn;

public sealed class LocalOcrProvider : IOcrProvider, IDisposable
{
    private RapidOcr? engine;
    private readonly SemaphoreSlim gate = new(1);
    private (string Source, string Game, int Width, int Height, PixelRect Region)? previousKey;
    private SKRectI? textRegion;
    private string? textHash;
    private IReadOnlyList<RecognizedLine> previousLines = Array.Empty<RecognizedLine>();
    private long lastFullScan;
    public bool PreferTextRegions { get; set; } = true;
    public string Name => "本地 PP-OCRv5 · 离线";
    public Task<RecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) => RecognizeCore(frame, false, cancellationToken);
    public Task<RecognitionResult> RecognizeAutomaticAsync(CapturedFrame frame, CancellationToken cancellationToken) => RecognizeCore(frame, PreferTextRegions, cancellationToken);
    private async Task<RecognitionResult> RecognizeCore(CapturedFrame frame, bool adaptive, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                if (engine is null)
                {
                    var candidate = new RapidOcr();
                    try
                    {
                        var dir = Path.Combine(AppContext.BaseDirectory, "models", "v5");
                        using var options = RapidOcr.GetDefaultSessionOptions(2);
                        candidate.InitModels(Path.Combine(dir, "ch_PP-OCRv5_mobile_det.onnx"), Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                            Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx"), Path.Combine(dir, "ppocrv5_latin_dict.txt"), options);
                        engine = candidate;
                    }
                    catch { candidate.Dispose(); throw; }
                }
                using var bitmap = frame.Pixels?.Open() ?? SKBitmap.Decode(frame.OcrPng);
                var key = (frame.SourceKey ?? frame.Source, frame.Game, frame.Width, frame.Height, frame.Region);
                var useRegion = adaptive && previousKey == key && textRegion is not null && Stopwatch.GetElapsedTime(lastFullScan) < TimeSpan.FromSeconds(3);
                using var cropped = useRegion ? Crop(bitmap, textRegion!.Value) : null;
                var hash = cropped is null ? null : Convert.ToHexString(SHA256.HashData(cropped.Bytes));
                if (cropped is not null && hash == textHash)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new RecognitionResult(frame, previousLines, Name, watch.Elapsed) { ScanMode = "文字未变化" };
                }
                var offset = useRegion ? textRegion!.Value : new SKRectI(0, 0, bitmap.Width, bitmap.Height);
                var lines = Detect(cropped ?? bitmap, frame, offset.Left, offset.Top);
                if (useRegion && (lines.Count < previousLines.Count || previousLines.Any(p => p.Words.Count() == 1 && !lines.Any(l => l.Text == p.Text))
                    || lines.Count == 0 || lines.Any(l => l.Bounds.X <= frame.Region.X + offset.Left + 4
                    || l.Bounds.Y <= frame.Region.Y + offset.Top + 4 || l.Bounds.X + l.Bounds.Width >= frame.Region.X + offset.Right - 4
                    || l.Bounds.Y + l.Bounds.Height >= frame.Region.Y + offset.Bottom - 4)))
                { useRegion = false; lines = Detect(bitmap, frame, 0, 0); }
                cancellationToken.ThrowIfCancellationRequested();
                previousKey = key; previousLines = lines;
                if (!useRegion)
                {
                    lastFullScan = Stopwatch.GetTimestamp(); textRegion = TextRegion.Around(lines, frame);
                    using var region = textRegion is { } rect ? Crop(bitmap, rect) : null;
                    textHash = region is null ? null : Convert.ToHexString(SHA256.HashData(region.Bytes));
                }
                else textHash = hash;
                return new RecognitionResult(frame, lines, Name, watch.Elapsed) { ScanMode = useRegion ? "文字区域" : "全区域" };
            }, cancellationToken);
        }
        finally { gate.Release(); }
    }
    private IReadOnlyList<RecognizedLine> Detect(SKBitmap bitmap, CapturedFrame frame, int x, int y)
    {
        var result = engine!.Detect(bitmap, RapidOcrOptions.Default with { DoAngle = false, ImgResize = 1536, TextScore = 0.65f });
        return result.TextBlocks.Select(b =>
        {
            var xs = b.BoxPoints.Select(p => (double)p.X).ToArray(); var ys = b.BoxPoints.Select(p => (double)p.Y).ToArray();
            return new RecognizedLine(b.Text, b.CharScores?.Average() ?? 0, new(xs.Min() + frame.Region.X + x,
                ys.Min() + frame.Region.Y + y, xs.Max() - xs.Min(), ys.Max() - ys.Min()));
        }).ToArray();
    }
    private static SKBitmap Crop(SKBitmap bitmap, SKRectI rect)
    {
        using var subset = new SKBitmap();
        if (!bitmap.ExtractSubset(subset, rect)) throw new InvalidDataException("文字区域超出截图。");
        return subset.Copy(); // Tight rows: animated pixels outside the region must not enter its fingerprint.
    }
    public void Dispose() { engine?.Dispose(); gate.Dispose(); }
}

public sealed class OcrRateLimitException(TimeSpan retryAfter) : Exception($"在线 OCR 已限流，请在 {Math.Ceiling(retryAfter.TotalSeconds)} 秒后重试。")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
public sealed class OcrAuthenticationException() : Exception("官方 OCR 令牌无效或没有权限，请检查设置中的 AI Studio Access Token。");

public sealed class PaddleCloudOcrProvider : IOcrProvider
{
    public const string OfficialBaseUrl = "https://paddleocr.aistudio-app.com";
    private readonly HttpClient client;
    private readonly string token;
    private readonly TimeSpan timeout;
    private readonly string baseUrl;
    private readonly TimeSpan pollInterval;
    public string Name => "官方 PP-OCRv6 · 在线";
    public PaddleCloudOcrProvider(HttpClient client, string token, TimeSpan timeout, string baseUrl = OfficialBaseUrl, TimeSpan? pollInterval = null)
    {
        this.client = client; this.token = token; this.timeout = timeout; this.baseUrl = baseUrl.TrimEnd('/');
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    }
    public async Task<RecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请先在设置中填写官方 OCR Access Token，或选择本地 OCR。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("PP-OCRv6"), "model");
            form.Add(new StringContent("{\"useDocOrientationClassify\":false,\"useDocUnwarping\":false,\"visualize\":false}"), "optionalPayload");
            var image = new ByteArrayContent(frame.OcrPng); image.Headers.ContentType = new("image/png");
            form.Add(image, "file", "game-frame.png");
            using var submit = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v2/ocr/jobs") { Content = form };
            var job = await FetchData(submit, deadline.Token);
            var jobId = job.GetProperty("jobId").GetString();
            if (string.IsNullOrEmpty(jobId)) throw new InvalidDataException("官方 OCR 返回了空任务编号。");
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v2/ocr/jobs/" + Uri.EscapeDataString(jobId));
                var status = await FetchData(request, deadline.Token);
                var state = status.GetProperty("state").GetString();
                if (state == "failed") throw new InvalidOperationException("官方 OCR 任务失败，请检查服务配额或稍后重试。");
                if (state == "done")
                {
                    var url = status.GetProperty("resultUrl").GetProperty("jsonUrl").GetString();
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                        throw new InvalidDataException("官方 OCR 结果下载地址无效。");
                    // Result resources use signed URLs. Never send the user's token to the resource host.
                    using var download = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = await client.SendAsync(download, deadline.Token);
                    CheckStatus(response);
                    var jsonl = await response.Content.ReadAsStringAsync(deadline.Token);
                    return new(frame, ParseResult(jsonl, frame.Region), Name, watch.Elapsed);
                }
                if (state is not ("pending" or "running")) throw new InvalidDataException("官方 OCR 返回了未知任务状态。");
                await Task.Delay(pollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException($"在线 OCR 等待超过 {timeout.TotalSeconds:0} 秒。可在设置中增加超时或改用本地识别。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接官方 OCR 服务，请检查网络。未自动重复提交任务。"); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException && e.Message.StartsWith("The requested"))
        { throw new InvalidDataException("官方 OCR 响应格式不符合预期。", e); }
    }
    private async Task<JsonElement> FetchData(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct); CheckStatus(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            throw new InvalidOperationException($"官方 OCR 请求失败，错误码 {code.GetInt32()}。请检查令牌、配额与服务状态。");
        return root.GetProperty("data").Clone();
    }
    private static void CheckStatus(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new OcrAuthenticationException();
        if ((int)response.StatusCode == 429)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
            throw new OcrRateLimitException(retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1));
        }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"官方 OCR 服务返回 HTTP {(int)response.StatusCode}，未重复提交。");
    }
    public static IReadOnlyList<RecognizedLine> ParseResult(string jsonl, PixelRect region)
    {
        var lines = new List<RecognizedLine>();
        foreach (var row in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var json = JsonDocument.Parse(row);
            foreach (var page in json.RootElement.GetProperty("result").GetProperty("ocrResults").EnumerateArray())
            {
                var pruned = page.GetProperty("prunedResult");
                var texts = pruned.GetProperty("rec_texts");
                var scores = pruned.GetProperty("rec_scores");
                var hasPolys = pruned.TryGetProperty("rec_polys", out var polygons);
                var hasBoxes = pruned.TryGetProperty("rec_boxes", out var boxes);
                for (var i = 0; i < texts.GetArrayLength(); i++)
                {
                    PixelRect bounds;
                    if (hasPolys && polygons.GetArrayLength() > i)
                    {
                        var points = polygons[i].EnumerateArray().Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToArray();
                        bounds = new(region.X + points.Min(p => p.X), region.Y + points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
                    }
                    else if (hasBoxes && boxes.GetArrayLength() > i)
                    {
                        var box = boxes[i]; bounds = new(region.X + box[0].GetDouble(), region.Y + box[1].GetDouble(), box[2].GetDouble() - box[0].GetDouble(), box[3].GetDouble() - box[1].GetDouble());
                    }
                    else throw new InvalidDataException("OCR 结果缺少文本位置，无法正确关联场景。");
                    lines.Add(new(texts[i].GetString() ?? "", scores[i].GetDouble(), bounds));
                }
            }
        }
        return lines;
    }
}
