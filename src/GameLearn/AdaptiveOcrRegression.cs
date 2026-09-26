using System.Text.Json;
using SkiaSharp;

namespace GameLearn;

internal static class AdaptiveOcrRegression
{
    public static async Task RunAsync(string output)
    {
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(output, "adaptive-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException(name);
        }
        CapturedFrame Frame(bool top, bool bottom, string source = "fixture", SKColor? background = null)
        {
            using var bitmap = new SKBitmap(1200, 720); bitmap.Erase(background ?? SKColors.DarkSlateGray);
            using var canvas = new SKCanvas(bitmap);
            using var face = SKTypeface.FromFamilyName("Segoe UI"); using var font = new SKFont(face, 36);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            if (top) canvas.DrawText("An ancient lighthouse.", 120, 120, font, paint);
            if (bottom) canvas.DrawText("Hidden treasure awaits.", 650, 630, font, paint);
            return CaptureService.CreateFrame(bitmap, "test", source, retainPixels: true);
        }
        using var provider = new LocalOcrProvider();
        var a = Frame(true, false);
        var first = await provider.RecognizeAutomaticAsync(a, CancellationToken.None);
        Check("First automatic sample scans full selected area", first.ScanMode == "全区域" && first.Lines.Any(l => l.Text.Contains("lighthouse")));
        var reused = await provider.RecognizeAutomaticAsync(a with { Id = Guid.NewGuid() }, CancellationToken.None);
        Check("Unchanged text pixels skip model while preserving latest frame", reused.ScanMode == "文字未变化" && reused.Frame.Id != a.Id);
        var appeared = Frame(true, true);
        await Task.Delay(3100);
        var periodic = await provider.RecognizeAutomaticAsync(appeared, CancellationToken.None);
        Check("Periodic full scan discovers new text outside prior region", periodic.ScanMode == "全区域" && periodic.Lines.Any(l => l.Text.Contains("treasure")));
        await provider.RecognizeAsync(a, CancellationToken.None);
        var moved = await provider.RecognizeAutomaticAsync(Frame(false, true), CancellationToken.None);
        Check("Missing dialogue triggers immediate full scan at its new location", moved.ScanMode == "全区域" && moved.Lines.Any(l => l.Text.Contains("treasure")));
        var switched = await provider.RecognizeAutomaticAsync(Frame(true, false, "new-source"), CancellationToken.None);
        Check("Source switch does not reuse old text", switched.ScanMode == "全区域" && switched.Lines.Any(l => l.Text.Contains("lighthouse")));
        var manual = await provider.RecognizeAsync(Frame(true, true, "new-source"), CancellationToken.None);
        Check("Manual request always scans full area", manual.ScanMode == "全区域" && manual.Lines.Any(l => l.Text.Contains("treasure")));
        provider.PreferTextRegions = false;
        var disabled = await provider.RecognizeAutomaticAsync(Frame(true, true, "new-source"), CancellationToken.None);
        Check("Disabling acceleration forces complete recognition", disabled.ScanMode == "全区域");
    }
}
