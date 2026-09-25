using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace GameLearn;

internal static class FloatingRegression
{
    public static async Task RunAsync(string output)
    {
        Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-data"));
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(output, "floating-results.json"), JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException("Floating regression: " + name);
        }
        var vm = new MainViewModel(); vm.SelectedSource = new(0, "ns", "OBS", CaptureSourceKind.ObsInput, "ns", true);
        var captureCount = 0; var recalls = 0; var expands = 0; var hides = 0; var definitions = 0; var wordbooks = 0; var selectionToggles = 0; var sources = 0;
        var bar = new FloatingToolbar(vm, () => { captureCount++; return Task.CompletedTask; }, () => recalls++, () => expands++, () => hides++, () => definitions++, () => wordbooks++, enabled => { selectionToggles += enabled ? 1 : -1; return true; }, () => sources++);
        LookupWindow? card = null;
        WordbookWindow? book = null;
        try
        {
            vm.Status = "准备就绪 · 点击识别，遇见生词就记下来";
            Render((FrameworkElement)bar.Content, bar.Width, bar.Height, Path.Combine(output, "floating-toolbar.png"));
            Check("Expanded floating window includes room for current words", bar.Width == 680 && bar.Height == 460 && !bar.ShowInTaskbar && bar.Topmost);
            var handle = new WindowInteropHelper(bar).EnsureHandle(); // Hidden native window only; never shown on the user's desktop.
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            Check("Native toolbar uses no-activate tool-window styles", (style & 0x08000000) != 0 && (style & 0x80) != 0 && !bar.ShowActivated && !bar.IsVisible);
            foreach (var name in new[] { "CaptureButton", "RecallButton", "ExpandButton", "WordbookButton", "HideButton" })
                ((Button)bar.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Toolbar buttons dispatch capture, recall, expand, wordbook and hide", captureCount == 1 && recalls == 1 && expands == 1 && wordbooks == 1 && hides == 1);
            ((Button)bar.FindName("SourceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Floating toolbar opens the source picker directly", sources == 1);
            var selection = (ToggleButton)bar.FindName("SelectionSwitch");
            selection.SetCurrentValue(ToggleButton.IsCheckedProperty, true); selection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check("Selection mode is exposed as a switch in the floating toolbar", selectionToggles == 1 && selection.IsChecked == true);
            selection.SetCurrentValue(ToggleButton.IsCheckedProperty, false); selection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var automatic = (ToggleButton)bar.FindName("AutomaticSwitch");
            automatic.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Check("Automatic switch writes through to the shared view model", vm.IsAutomatic);
            automatic.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check("Enabling automatic mode seeds an empty word panel immediately", captureCount == 2);
            automatic.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Check("Automatic switch turns the same scheduler off", !vm.IsAutomatic);
            bar.SetCollapsed(true);
            Render((FrameworkElement)bar.Content, bar.Width, bar.Height, Path.Combine(output, "floating-toolbar-collapsed.png"));
            Check("Collapsed state is compact and persisted", bar.Width == 64 && bar.Height == 64 && AppSettings.Load().FloatingBarCollapsed);
            bar.SetCollapsed(false);
            Check("Expanding restores toolbar layout", bar.Width == 680 && !AppSettings.Load().FloatingBarCollapsed);
            using var fixture = new SKBitmap(640, 360); fixture.Erase(new SKColor(36, 58, 52));
            var frame = CaptureService.CreateFrame(fixture, "游戏示例", "fixture");
            var line = new RecognizedLine("A little elbow grease can repair the wrecked ship.", 0.99, new(30, 100, 570, 35));
            var speaker = new RecognizedLine("Valdi", 0.99, new(30, 30, 80, 20));
            var initial = new RecognitionResult(frame, new[] { speaker, line }, "fixture", TimeSpan.Zero);
            vm.Present(initial);
            vm.Status = "已识别当前游戏画面 · 点词即可查看释义";
            Render((FrameworkElement)bar.Content, bar.Width, bar.Height, Path.Combine(output, "floating-words.png"));
            var wordsControl = (ItemsControl)bar.FindName("CurrentWordLines");
            Check("All recognized lines are visible in the floating window", wordsControl.Items.Count == 2);
            var wordButton = Descendants<Button>((DependencyObject)bar.Content).FirstOrDefault(b => b.DataContext is string word && word == "wrecked");
            Check("Words from a non-selected dialogue line are directly clickable", wordButton is not null && ReferenceEquals(vm.SelectedLine, speaker));
            wordButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Direct word click selects its own original line and opens definition", definitions == 1 && vm.DetailWord == "wreck" && ReferenceEquals(vm.SelectedLine, line));
            Check("Direct word click records the matching displayed screenshot", vm.SelectedEncounter!.ImagePath.EndsWith(frame.Id + ".png", StringComparison.Ordinal));
            card = new LookupWindow(vm, compact: true);
            card.ShowPage(false);
            Render((FrameworkElement)card.Content, card.Width - 16, card.Height - 40, Path.Combine(output, "floating-lookup.png"));
            var lookupTabs = Descendants<TabControl>((DependencyObject)card.Content).Single();
            Check("Floating lookup uses a compact two-page layout", card.IsCompact && card.Width == 460 && lookupTabs.Items.Count == 2);
            card.ShowPage(true);
            Render((FrameworkElement)card.Content, card.Width - 16, card.Height - 40, Path.Combine(output, "floating-definition.png"));
            Check("Recall/word page is available without opening the full dashboard", lookupTabs.SelectedIndex == 1 && vm.DetailWord == "wreck");
            Check("Hidden toolbar rendering leaves interaction protection released", !vm.IsAutomaticRefreshPaused);
            vm.Store.SaveRecentRecognition(initial);
            book = new WordbookWindow(vm, true, () => expands++);
            book.ShowRecent();
            Render((FrameworkElement)book.Content, book.Width, book.Height, Path.Combine(output, "recent-recognition.png"));
            Check("Wordbook has custom chrome and persistent recent sentences", book.WindowStyle == WindowStyle.None && book.Topmost
                && ((ItemsControl)book.FindName("RecentItems")).Items.Count == 1 && ((TabControl)book.FindName("Pages")).SelectedIndex == 1);
            var bookPages = (TabControl)book.FindName("Pages"); bookPages.SelectedIndex = 0;
            Render((FrameworkElement)book.Content, book.Width, book.Height, Path.Combine(output, "wordbook-compact.png"));
            book.SetCompact(false);
            Render((FrameworkElement)book.Content, book.Width, book.Height, Path.Combine(output, "wordbook-expanded.png"));
            Check("Wordbook switches between compact overlay and expanded layout", book.Width == 900 && !book.Topmost);
            ((Button)book.FindName("ToolbarButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Wordbook returns directly to floating toolbar", expands == 2); book = null;
            vm.SelectedSource = null;
            vm.Present(initial);
            Render((FrameworkElement)bar.Content, bar.Width, bar.Height, Path.Combine(output, "floating-words-fixed.png"));
            wordButton = Descendants<Button>((DependencyObject)bar.Content).First(b => b.DataContext is string word && word == "wrecked");
            var hold = new object(); vm.SetPresentationInteraction(hold, true); vm.Scheduler.SetAutomatic(true);
            var newerFrame = CaptureService.CreateFrame(fixture, "游戏示例", "fixture");
            var newer = new RecognitionResult(newerFrame, new[] { new RecognizedLine("Another wrecked boat.", 0.99, line.Bounds) }, "fixture", TimeSpan.Zero);
            await vm.Scheduler.EnqueueAsync(new(newerFrame, TriggerKind.Automatic, vm.Scheduler.Generation, new FixedProvider(newer)));
            Check("Current word rows remain fixed while newer automatic results arrive", wordsControl.Items.Count == 2 && wordsControl.Items.Cast<PrioritizedLine>().Any(item => ReferenceEquals(item.Line, line)));
            wordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Click during protected interaction keeps the original scene", definitions == 2 && vm.SelectedEncounter!.ImagePath.EndsWith(frame.Id + ".png", StringComparison.Ordinal));
            vm.SetPresentationInteraction(hold, false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(vm.FlushAutomaticPresentation, DispatcherPriority.Background);
            Check("Floating word rows resume with the latest result", wordsControl.Items.Count == 1 && ((PrioritizedLine)wordsControl.Items[0]).Line.Text == "Another wrecked boat.");
            wordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("A stale word button cannot save a different frame", definitions == 2);
            vm.Scheduler.SetAutomatic(false);
            vm.Present(new(newerFrame, new[] { new RecognizedLine("42 / 100", 0.99, line.Bounds) }, "fixture", TimeSpan.Zero));
            Check("Numeric-only OCR shows an actionable empty state", wordsControl.Items.Count == 0 && ((Border)bar.FindName("EmptyWords")).Visibility == Visibility.Visible);
            var fragmented = new RecognitionResult(newerFrame, new[] {
                new RecognizedLine("A little elbow grease can repair", .99, new(30, 100, 400, 20)),
                new RecognizedLine("the wrecked ship.", .99, new(30, 126, 250, 20)),
                new RecognizedLine("The archaeologist deciphered the inscription.", .99, new(30, 190, 570, 20)),
                new RecognizedLine("You, you can do it!", .99, new(30, 250, 300, 20)),
                new RecognizedLine("We should gather provisions before the expedition.", .99, new(30, 310, 570, 20)) }, "fixture", TimeSpan.Zero);
            await vm.Scheduler.EnqueueAsync(new(newerFrame, TriggerKind.Manual, vm.Scheduler.Generation, new FixedProvider(fragmented)));
            Render((FrameworkElement)bar.Content, bar.Width, bar.Height, Path.Combine(output, "floating-sentences.png"));
            Check("OCR fragments become one complete clickable sentence through the scheduler", wordsControl.Items.Count == 4
                && wordsControl.Items.Cast<PrioritizedLine>().Any(item => item.Text == "A little elbow grease can repair the wrecked ship."));
        }
        finally { book?.Close(); card?.Close(); bar.Close(); await vm.ShutdownAsync(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class FixedProvider(RecognitionResult result) : IOcrProvider
    {
        public string Name => "fixture";
        public Task<RecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken token) => Task.FromResult(result);
    }
    private static void Render(FrameworkElement content, double width, double height, string path)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
