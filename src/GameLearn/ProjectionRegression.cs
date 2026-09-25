using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace GameLearn;

internal static class ProjectionRegression
{
    public static async Task RunAsync(string output)
    {
        Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-data"));
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(output, "projection-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException(name);
        }
        var vm = new MainViewModel();
        SourcePickerWindow? picker = null; WordSelectionOverlay? overlay = null;
        try
        {
            picker = new(vm);
            var projector = new WindowSource(123, "全屏投影（预览）- 显示器 2", "obs64");
            var game = new WindowSource(124, "Game window", "game");
            vm.Sources.Clear();
            foreach (var source in new[] { projector, game, new WindowSource(125, "OBS Studio", "obs64"),
                new WindowSource(0, "ns", "OBS", CaptureSourceKind.ObsInput, "ns", true),
                new WindowSource(0, "备用视频源", "OBS", CaptureSourceKind.ObsInput, "video"),
                new WindowSource(0, "switch2", "OBS", CaptureSourceKind.ObsScene, "switch2") }) vm.Sources.Add(source);
            vm.SelectedSource = game;
            picker.RefreshGroups();
            Render((FrameworkElement)picker.Content, 690, 650, Path.Combine(output, "source-picker.png"));
            Check("Projectors separated from OBS interface and unused sources", ((ItemsControl)picker.FindName("ProjectorItems")).Items.Count == 1
                && ((ItemsControl)picker.FindName("InterfaceItems")).Items.Count == 1 && !((Expander)picker.FindName("OtherGroup")).IsExpanded);
            ((TextBox)picker.FindName("SearchBox")).Text = "备用";
            Check("Search reveals collapsed sources without changing selection", ((ItemsControl)picker.FindName("OtherItems")).Items.Count == 1
                && ((Expander)picker.FindName("OtherGroup")).IsExpanded && vm.SelectedSource == game);
            ((TextBox)picker.FindName("SearchBox")).Text = "";
            Render((FrameworkElement)picker.Content, 690, 650, Path.Combine(output, "source-picker.png"));
            Descendants<Button>((DependencyObject)picker.Content).Single(b => Equals(b.DataContext, projector)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Choosing projector selects its window handle", vm.SelectedSource == projector);
            picker = null;
            using var bitmap = new SKBitmap(960, 540); bitmap.Erase(new SKColor(28, 46, 52));
            using (var drawing = new SKCanvas(bitmap))
            using (var paint = new SKPaint { Color = SKColors.Black })
            {
                drawing.DrawRect(0, 0, 960, 60, paint); drawing.DrawRect(0, 480, 960, 60, paint);
            }
            var frame = CaptureService.CreateFrame(bitmap, "Projection fixture", projector.Title) with
                { SourceKey = projector.Key, DesktopBounds = new(-1920, 0, 960, 540), CapturedClientOnly = true };
            var line = new RecognizedLine("The wrecked ship.", .99, new(100, 400, 380, 25));
            var result = new RecognitionResult(frame, new[] { line }, "fixture", TimeSpan.Zero);
            vm.Present(result); var definitions = 0;
            overlay = new(vm, projector, result, () => definitions++);
            Check("Live overlay contains no screenshot and cannot activate the game window", overlay.Content is Canvas { Background: null } && !overlay.ShowActivated && !vm.IsAutomaticRefreshPaused);
            var overlayHandle = new System.Windows.Interop.WindowInteropHelper(overlay).EnsureHandle();
            Check("Native overlay uses no-activate style", (GetWindowLongPtr(overlayHandle, -20).ToInt64() & 0x08000000) != 0 && !overlay.IsVisible);
            overlay.Measure(new Size(960, 540)); overlay.Arrange(new Rect(0, 0, 960, 540));
            Render((FrameworkElement)overlay.Content, 960, 540, Path.Combine(output, "projection-overlay.png"));
            using (var transparent = SKBitmap.Decode(Path.Combine(output, "projection-overlay.png")))
                Check("Empty game area is fully transparent for layered-window click-through", transparent.GetPixel(30, 300).Alpha == 0);
            overlay.RenderLines();
            var target = Descendants<Button>((DependencyObject)overlay.Content).Single(b => ReferenceEquals(b.Tag, line));
            Check("OCR line maps below the black bar without invented word boxes", Canvas.GetLeft(target) == 100 && Canvas.GetTop(target) == 400 && target.Width == 380);
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render((FrameworkElement)overlay.Content, 960, 540, Path.Combine(output, "projection-overlay.png"));
            var word = Descendants<Button>((DependencyObject)overlay.Content).Single(b => Equals(b.Content, "wrecked"));
            var newer = result with { Frame = frame with { Id = Guid.NewGuid(), Timestamp = frame.Timestamp.AddSeconds(2) },
                Lines = new[] { new RecognizedLine("A beautiful garden.", .99, new(110, 410, 370, 25)) } };
            vm.Present(newer);
            word.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Changed game sentence immediately invalidates old word choices", definitions == 0 && vm.Store.Words().Count == 0);
            var latestTarget = Descendants<Button>((DependencyObject)overlay.Content).Single(b => b.Tag is RecognizedLine l && l.Text == "A beautiful garden.");
            latestTarget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render((FrameworkElement)overlay.Content, 960, 540, Path.Combine(output, "latest-word-choices.png"));
            var latestWord = Descendants<Button>((DependencyObject)overlay.Content).Single(b => Equals(b.Content, "garden"));
            var latestFrame = newer.Frame with { Id = Guid.NewGuid(), Timestamp = newer.Frame.Timestamp.AddSeconds(1) };
            var movedLine = newer.Lines[0] with { Bounds = new(120, 420, 370, 25) };
            vm.Present(newer with { Frame = latestFrame, Lines = new[] { movedLine } });
            latestWord.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Unchanged sentence still saves the newest frame when word is selected", definitions == 1 && vm.DetailWord == "garden"
                && vm.SelectedEncounter!.ImagePath.EndsWith(latestFrame.Id + ".png") && vm.SelectedEncounter.Bounds == movedLine.Bounds);
            overlay.Suspend();
            Check("Capture outage removes stale click targets", !Descendants<Button>((DependencyObject)overlay.Content).Any());
            overlay.UpdateResult(newer with { Frame = latestFrame }); overlay.RenderLines();
            Check("Capture recovery restores live targets", Descendants<Button>((DependencyObject)overlay.Content).Any(b => b.Tag is RecognizedLine));
            var count = vm.Store.History(vm.SelectedWord!.Id).Count;
            vm.SelectedSource = game;
            word.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Changing source invalidates all old selection callbacks", definitions == 1 && !vm.IsAutomaticRefreshPaused
                && vm.Store.History(vm.SelectedWord!.Id).Count == count);
            overlay = null;
            vm.Scheduler.SetAutomatic(true);
            vm.HandleAutomaticFailure(new InvalidOperationException("黑画面"), false);
            Check("Transient capture error retains automatic mode and schedules recovery", vm.IsAutomatic && vm.AutomaticRetryAt > DateTimeOffset.UtcNow);
            await vm.Scheduler.EnqueueAsync(new(newer.Frame, TriggerKind.Automatic, vm.Scheduler.Generation, new FixedProvider(newer)));
            Check("Successful automatic recognition clears retry state", vm.IsAutomatic && vm.AutomaticRetryAt is null);
            vm.HandleAutomaticFailure(new TimeoutException("OCR 暂时超时"), true);
            Check("Local OCR error also schedules automatic recovery", vm.IsAutomatic && vm.AutomaticRetryAt is not null);
            vm.IsAutomatic = false;
            Check("User stop cancels automatic recovery", vm.AutomaticRetryAt is null && !vm.IsAutomatic);
            var nativeProjector = CaptureService.ListWindows().FirstOrDefault(s => s.IsObsProjector);
            if (nativeProjector is not null)
            {
                using var capture = new CaptureService();
                var actual = await capture.CaptureAsync(nativeProjector, "OBS 投影实测", null, CancellationToken.None);
                File.WriteAllBytes(Path.Combine(output, "live-projector.png"), actual.FullPng);
                Check("Existing OBS projector captures with physical screen coordinates", actual.DesktopBounds is not null && actual.SourceKey == nativeProjector.Key);
                using var local = new LocalOcrProvider();
                var recognized = await local.RecognizeAsync(actual, CancellationToken.None);
                recognized = recognized with { Lines = SentenceAssembler.Merge(recognized.Lines) };
                vm.SelectedSource = nativeProjector; vm.Present(recognized);
                overlay = new(vm, nativeProjector, recognized, () => definitions++);
                Render((FrameworkElement)overlay.Content, actual.Width / 1.25, actual.Height / 1.25, Path.Combine(output, "live-projector-selection.png"));
                overlay.RenderLines();
                Render((FrameworkElement)overlay.Content, actual.Width / 1.25, actual.Height / 1.25, Path.Combine(output, "live-projector-selection.png"));
                Check("Local OCR projector lines render on the matching snapshot", recognized.Lines.Count > 0
                    && Descendants<Button>((DependencyObject)overlay.Content).Any(b => b.Tag is RecognizedLine));
                File.WriteAllText(Path.Combine(output, "live-projector-details.json"), JsonSerializer.Serialize(new {
                    nativeProjector.Title, actual.Width, actual.Height, actual.DesktopBounds, capture.LastBackend,
                    ocrMilliseconds = recognized.Elapsed.TotalMilliseconds, lines = recognized.Lines.Select(l => new { l.Text, l.Bounds })
                }, new JsonSerializerOptions { WriteIndented = true }));
                overlay.Close(); overlay = null;
            }
            else File.WriteAllText(Path.Combine(output, "live-projector-status.txt"), "No visible OBS projector was open; native full-screen test was not run. No windows were opened or moved.");
        }
        finally { overlay?.Close(); picker?.Close(); await vm.ShutdownAsync(); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    private sealed class FixedProvider(RecognitionResult result) : IOcrProvider
    {
        public string Name => "recovery-fixture";
        public Task<RecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken token) => Task.FromResult(result);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target) yield return target;
            foreach (var match in Descendants<T>(child)) yield return match;
        }
    }
    private static void Render(FrameworkElement content, double width, double height, string path)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
