using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;

namespace GameLearn;

public static class Diagnostics
{
    public static async Task RunAsync(string[] args)
    {
        var output = Argument(args, "--output") ?? Path.Combine(AppSettings.DataDirectory, "diagnostics"); Directory.CreateDirectory(output);
        if (args.Contains("--interaction-regression")) { await InteractionRegression.RunAsync(output); return; }
        if (args.Contains("--obs-direct")) { await ObsDiagnostics.RunAsync(output); return; }
        if (args.Contains("--presentation-regression")) { await PresentationRegression.RunAsync(output); return; }
        if (args.Contains("--floating-regression")) { await FloatingRegression.RunAsync(output); return; }
        if (args.Contains("--projection-regression")) { await ProjectionRegression.RunAsync(output); return; }
        if (args.Contains("--performance-regression")) { await PerformanceRegression.RunAsync(output, Argument(args, "--samples")); return; }
        if (args.Contains("--adaptive-regression")) { await AdaptiveOcrRegression.RunAsync(output); return; }
        if (args.Contains("--ai-settings-regression")) { await AiSettingsRegression.RunAsync(output); return; }
        if (args.Contains("--test-saved-ai"))
        {
            var settings = AppSettings.Load(); using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var watch = Stopwatch.StartNew();
            var reply = await new AiExplanationService(client).TestConnectionAsync(settings, CancellationToken.None);
            File.WriteAllText(Path.Combine(output, "saved-ai-test.json"), JsonSerializer.Serialize(new { success = reply.Length > 0, model = settings.AiModel,
                path = AiExplanationService.ResolveEndpoint(settings.AiBaseUrl).AbsolutePath, elapsedMs = watch.Elapsed.TotalMilliseconds }));
            return;
        }
        if (args.Contains("--close-regression"))
        {
            Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-data"));
            System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow(); window.Show();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(Path.Combine(output, "close-results.json"), "{\"idleWindowClosesWithoutReentry\":true}");
            return;
        }
        if (args.Contains("--integration")) { await IntegrationAsync(output); return; }
        using var capture = new CaptureService(); using var ocr = new LocalOcrProvider();
        CropRegion? crop = null;
        if (Argument(args, "--crop") is { } cropText)
        {
            var values = cropText.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (values.Length != 4) throw new ArgumentException("--crop requires x,y,width,height normalized coordinates");
            crop = new(values[0], values[1], values[2], values[3]);
        }
        CapturedFrame frame;
        var input = Argument(args, "--image");
        if (input is not null)
        {
            using var bitmap = SKBitmap.Decode(input); frame = CaptureService.CreateFrame(bitmap, "OBS 实测", "离线截图", crop);
        }
        else
        {
            var source = CaptureService.ListWindows().FirstOrDefault(w => w.ProcessName == "obs64" && w.Title.Contains("Projector"))
                ?? CaptureService.ListWindows().FirstOrDefault(w => w.ProcessName == "obs64") ?? throw new InvalidOperationException("没有 OBS 窗口可测试。");
            if (args.Contains("--printwindow"))
            {
                using var bitmap = CaptureService.CapturePrintWindow(source.Handle);
                frame = CaptureService.CreateFrame(bitmap, "OBS 实测", source.Title, crop);
            }
            else frame = await capture.CaptureAsync(source, "OBS 实测", crop, CancellationToken.None);
        }
        File.WriteAllBytes(Path.Combine(output, "captured.png"), frame.FullPng);
        var cold = await ocr.RecognizeAsync(frame, CancellationToken.None);
        var warm = await ocr.RecognizeAsync(frame, CancellationToken.None);
        using var dictionary = new OfflineDictionary();
        var report = new { source = frame.Source, backend = capture.LastBackend, width = frame.Width, height = frame.Height, coldMs = cold.Elapsed.TotalMilliseconds, warmMs = warm.Elapsed.TotalMilliseconds,
            lines = warm.Lines, dictionaryAvailable = dictionary.Available, words = warm.Lines.SelectMany(l => l.Words).Distinct(StringComparer.OrdinalIgnoreCase).Select(dictionary.Lookup).ToArray() };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var collectText = Argument(args, "--collect");
        if (int.TryParse(collectText, out var count) && count > 0)
        {
            var samples = new List<object>();
            var source = CaptureService.ListWindows().FirstOrDefault(w => w.ProcessName == "obs64" && w.Title.Contains("Projector")) ?? CaptureService.ListWindows().First(w => w.ProcessName == "obs64");
            for (var i = 0; i < Math.Min(count, 30); i++)
            {
                var sample = await capture.CaptureAsync(source, "OBS 实测", null, CancellationToken.None);
                var recognized = await ocr.RecognizeAsync(sample, CancellationToken.None);
                File.WriteAllBytes(Path.Combine(output, $"sample-{i + 1:00}.png"), sample.FullPng);
                samples.Add(new { index = i + 1, sample.Timestamp, sample.Width, sample.Height, elapsedMs = recognized.Elapsed.TotalMilliseconds, lines = recognized.Lines });
                await Task.Delay(1500);
            }
            File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        }
        using var marked = SKBitmap.Decode(frame.FullPng); using var canvas = new SKCanvas(marked); using var paint = new SKPaint { Color = SKColors.LightGreen, IsStroke = true, StrokeWidth = 3 };
        foreach (var line in warm.Lines) canvas.DrawRect(new SKRect((float)line.Bounds.X, (float)line.Bounds.Y, (float)(line.Bounds.X + line.Bounds.Width), (float)(line.Bounds.Y + line.Bounds.Height)), paint);
        using var file = File.Create(Path.Combine(output, "recognized.png")); marked.Encode(file, SKEncodedImageFormat.Png, 100);
        if (args.Contains("--ui"))
        {
            Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-ui-data"));
            var window = new MainWindow();
            window.Show(); window.Vm.Present(warm);
            var target = warm.Lines.FirstOrDefault(l => l.Words.Any(w => w.Equals("wrecked", StringComparison.OrdinalIgnoreCase)))
                ?? warm.Lines.FirstOrDefault(l => l.Words.Any(w => w.Equals("grease", StringComparison.OrdinalIgnoreCase))) ?? warm.Lines.FirstOrDefault();
            window.Vm.SelectedLine = target;
            if (target is not null) window.Vm.Learn(target.Words.FirstOrDefault(w => w.Equals("wrecked", StringComparison.OrdinalIgnoreCase) || w.Equals("grease", StringComparison.OrdinalIgnoreCase)) ?? target.Words.First());
            var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs");
            foreach (var (tab, name) in new[] { (0, "current"), (1, "vocabulary"), (2, "settings") })
            {
                tabs.SelectedIndex = tab;
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rendered.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
                using var screenshot = File.Create(Path.Combine(output, "ui-" + name + ".png")); encoder.Save(screenshot);
            }
            window.Close();
        }
    }
    private static string? Argument(string[] args, string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }

    private static async Task IntegrationAsync(string output)
    {
        Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-integration-data"));
        var main = new MainWindow(); main.Show();
        var text = new System.Windows.Controls.TextBlock { Text = "A little elbow grease can repair the wrecked ship.", FontSize = 32, Foreground = System.Windows.Media.Brushes.Black, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(40) };
        var target = new System.Windows.Window { Title = "GameLearn Capture Test", Width = 800, Height = 480, Background = System.Windows.Media.Brushes.White, Content = text };
        target.Show();
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(output, "integration-results.json"), JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException("Integration failed: " + name);
        }
        void CloseLookup() { foreach (var lookup in System.Windows.Application.Current.Windows.OfType<LookupWindow>().ToArray()) lookup.Close(); }
        var vm = main.Vm;
        var source = new WindowSource(new System.Windows.Interop.WindowInteropHelper(target).Handle, target.Title, "GameLearn");
        vm.Sources.Add(source); vm.SelectedSource = source; vm.GameName = "Integration test";
        await target.Dispatcher.InvokeAsync(() => target.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check("Automatic starts disabled", !vm.IsAutomatic);
        await vm.RecognizeNowAsync();
        Check("Manual captures ordinary WPF window while automatic is off", vm.Result?.Lines.Any(l => l.Text.Contains("wrecked")) == true);
        Check("Manual opens interactive lookup", System.Windows.Application.Current.Windows.OfType<LookupWindow>().Any());
        vm.SelectedLine = vm.Lines.First(l => l.Text.Contains("wrecked")); vm.Learn("wrecked");
        Check("Offline lemma and definition", vm.SelectedWord?.Word == "wreck" && vm.DetailTranslation.Contains("失事"));
        Check("Context retains wrapped dialogue", vm.DetailSentence.Contains("elbow") && vm.DetailSentence.Contains("wrecked"));
        vm.Notes = "A remembered game scene"; vm.Mastered = true; vm.SaveWord(); vm.RefreshWords();
        Check("Notes and mastered state survive list refresh", vm.Store.Words().Single().Notes == vm.Notes && vm.Store.Words().Single().Mastered);
        vm.GameFilter = "Integration test"; vm.Search = "wreck";
        Check("Game and word filters", vm.Words.Count == 1);
        var original = vm.Frame!;
        var lookup = System.Windows.Application.Current.Windows.OfType<LookupWindow>().First();
        await lookup.Dispatcher.InvokeAsync(() => lookup.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        RenderWindow(lookup, Path.Combine(output, "lookup.png")); CloseLookup();
        var completed = 0; vm.Scheduler.Completed += (_, _) => completed++;
        vm.IsAutomatic = true; await Task.Delay(3500); vm.IsAutomatic = false;
        await Task.Delay(500); var stoppedCount = completed; await Task.Delay(1500);
        Check("Automatic produces observations", stoppedCount > 0);
        Check("Automatic off stops observations", completed == stoppedCount);
        target.Width = 960; target.Height = 560;
        await target.Dispatcher.InvokeAsync(() => target.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(300); // Let the DWM compositor commit the resize, not just WPF layout.
        await vm.PreviewAsync();
        File.WriteAllText(Path.Combine(output, "resize.json"), JsonSerializer.Serialize(new { originalWidth = original.Width, newWidth = vm.Frame!.Width, target.Width, target.ActualWidth, vm.Status }));
        Check("Window resize updates capture dimensions", vm.Frame!.Width > original.Width);
        vm.SetCrop(new(0.02, 0.02, 0.96, 0.7)); await vm.RecognizeNowAsync(); CloseLookup();
        Check("Crop is applied and manual still works after automatic off", vm.Frame!.Region.Width < vm.Frame.Width && vm.Result!.Lines.Any(l => l.Text.Contains("wrecked")));
        var beforeMinimize = vm.Frame.Id;
        target.WindowState = System.Windows.WindowState.Minimized; await vm.RecognizeNowAsync();
        Check("Minimized source does not generate a stale encounter", vm.Frame.Id == beforeMinimize && vm.Status.Contains("最小化"));
        target.WindowState = System.Windows.WindowState.Normal;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(target);
        var tabs = (System.Windows.Controls.TabControl)main.FindName("Tabs"); tabs.SelectedIndex = 1;
        await main.Dispatcher.InvokeAsync(() => main.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        RenderWindow(main, Path.Combine(output, "vocabulary.png"));
        File.WriteAllText(Path.Combine(output, "integration-results.json"), JsonSerializer.Serialize(new { dpi = dpi.DpiScaleX, checks }, new JsonSerializerOptions { WriteIndented = true }));
        target.Close(); main.Close();
    }
    private static void RenderWindow(System.Windows.Window window, string output)
    {
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = File.Create(output); encoder.Save(file);
    }
}
