using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameLearn;

internal static class ObsDiagnostics
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    public static async Task RunAsync(string output)
    {
        var settings = AppSettings.Load();
        using var obs = new ObsCaptureService(() => ObsConnectionOptions.FromSettings(settings));
        var sources = await obs.ListSourcesAsync(CancellationToken.None);
        var selected = sources.FirstOrDefault(s => s.Kind == CaptureSourceKind.ObsInput && s.IsActive)
            ?? sources.First(s => s.Kind == CaptureSourceKind.ObsProgram);
        var first = await obs.CaptureAsync(selected, "OBS 实测", null, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(output, "direct-source.png"), first.FullPng);
        using var ocr = new LocalOcrProvider();
        var recognized = await ocr.RecognizeAsync(first, CancellationToken.None);
        var window = System.Diagnostics.Process.GetProcessesByName("obs64").First().MainWindowHandle;
        var wasMinimized = IsIconic(window);
        CapturedFrame hidden;
        try
        {
            ShowWindow(window, 6); await Task.Delay(350);
            hidden = await obs.CaptureAsync(selected, "OBS 最小化实测", null, CancellationToken.None);
            File.WriteAllBytes(Path.Combine(output, "source-while-minimized.png"), hidden.FullPng);
        }
        finally { if (!wasMinimized) ShowWindow(window, 9); }
        var program = await obs.CaptureAsync(sources.First(s => s.Kind == CaptureSourceKind.ObsProgram), "OBS 输出实测", null, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(output, "program.png"), program.FullPng);
        File.WriteAllText(Path.Combine(output, "obs-direct-results.json"), JsonSerializer.Serialize(new
        {
            source = selected.ToString(), first.Width, first.Height, minimizedCapture = new { hidden.Width, hidden.Height },
            program = program.Source, ocrMs = recognized.Elapsed.TotalMilliseconds, lines = recognized.Lines,
            availableSources = sources.Select(s => new { s.Title, kind = s.Kind.ToString(), s.IsActive }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
