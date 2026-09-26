using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace GameLearn;

internal static class PerformanceRegression
{
    public static async Task RunAsync(string output, string? samples = null)
    {
        if (samples is null)
        {
        var projector = CaptureService.ListWindows().FirstOrDefault(s => s.IsObsProjector)
            ?? throw new InvalidOperationException("性能测试需要一个已打开的 OBS 投影窗口。");
        using (var capture = new CaptureService())
        {
            for (var i = 0; i < 10; i++)
            {
                var frame = await capture.CaptureAsync(projector, "Performance samples", null, CancellationToken.None);
                File.WriteAllBytes(Path.Combine(output, $"frame-{i:D2}.png"), frame.FullPng);
                await Task.Delay(500);
            }
        }
        }
        var files = Directory.GetFiles(samples ?? output, "frame-*.png").Order().Take(10).ToArray();
        using var baseline = new LocalOcrProvider(); using var optimized = new LocalOcrProvider();
        using (var warm = SKBitmap.Decode(files[0]))
        {
            var f = CaptureService.CreateFrame(warm, "test", "warm");
            await baseline.RecognizeAsync(f, CancellationToken.None); await optimized.RecognizeAsync(f, CancellationToken.None);
        }
        var baselineRows = new List<(double Total, RecognitionResult Result)>();
        foreach (var file in files)
        {
            using var bitmap = SKBitmap.Decode(file);
            var watch = Stopwatch.StartNew(); var frame = LegacyFrame(bitmap);
            var result = await baseline.RecognizeAsync(frame, CancellationToken.None);
            baselineRows.Add((watch.Elapsed.TotalMilliseconds, result));
        }
        var rows = new List<object>(); var fastTimes = new List<double>();
        for (var i = 0; i < files.Length; i++)
        {
            using var bitmap = SKBitmap.Decode(files[i]);
            var watch = Stopwatch.StartNew(); var frame = CaptureService.CreateFrame(bitmap, "test", "benchmark", retainPixels: true);
            var result = await optimized.RecognizeAutomaticAsync(frame, CancellationToken.None);
            var total = watch.Elapsed.TotalMilliseconds; fastTimes.Add(total);
            var oldWords = Tokens(baselineRows[i].Result); var newWords = Tokens(result);
            rows.Add(new { file = Path.GetFileName(files[i]), baselineMs = baselineRows[i].Total, optimizedMs = total,
                baselineOcrMs = baselineRows[i].Result.Elapsed.TotalMilliseconds, optimizedOcrMs = result.Elapsed.TotalMilliseconds,
                result.ScanMode, wordAgreement = 1.0 - Distance(oldWords, newWords) / (double)Math.Max(1, Math.Max(oldWords.Length, newWords.Length)),
                baselineText = baselineRows[i].Result.Lines.Select(l => l.Text), optimizedText = result.Lines.Select(l => l.Text) });
            // Match the normal one-second sampling cadence, including periodic complete scans.
            if (total < 1000) await Task.Delay((int)(1000 - total));
        }
        File.WriteAllText(Path.Combine(output, "performance-results.json"), JsonSerializer.Serialize(new {
            baselineAverageMs = baselineRows.Average(r => r.Total), optimizedAverageMs = fastTimes.Average(),
            note = "Ten real projector frames. Timings include frame preparation and OCR, exclude native capture and UI. Text agreement is against baseline OCR, not human ground truth.", rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static CapturedFrame LegacyFrame(SKBitmap bitmap)
    {
        using var region = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(region)) canvas.DrawBitmap(bitmap, 0, 0);
        using var full = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var partial = region.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = partial.ToArray();
        return new(Guid.NewGuid(), DateTimeOffset.Now, "test", "benchmark", full.ToArray(), bytes, bitmap.Width, bitmap.Height,
            new(0, 0, bitmap.Width, bitmap.Height), Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static string[] Tokens(RecognitionResult result) => Regex.Matches(string.Join(" ", result.Lines.Select(l => l.Text)).ToLowerInvariant(), @"[a-z]+(?:['’-][a-z]+)*").Select(m => m.Value).ToArray();
    private static int Distance(string[] a, string[] b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var row = new int[b.Length + 1]; row[0] = i;
            for (var j = 1; j <= b.Length; j++) row[j] = Math.Min(Math.Min(prev[j] + 1, row[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = row;
        }
        return prev[b.Length];
    }
}
