using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameLearn;

/// <summary>Transparent, non-activating OCR targets and word choices following the latest recognition.</summary>
public sealed class WordSelectionOverlay : Window
{
    private readonly MainViewModel vm;
    private readonly WindowSource source;
    private RecognitionResult result;
    private readonly Action showDefinition;
    private readonly long generation;
    private readonly Canvas canvas = new() { Background = null, ClipToBounds = true };
    private readonly Border wordPanel = new() { Background = new SolidColorBrush(Color.FromRgb(20, 36, 42)), CornerRadius = new(10), Padding = new(12), Visibility = Visibility.Collapsed };
    private readonly WrapPanel words = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private HwndSource? hwnd;
    private PixelRect? placed;
    private bool closed, popupOpen, unavailable;
    private long choiceVersion;
    private RecognizedLine? selectedLine;

    public WordSelectionOverlay(MainViewModel vm, WindowSource source, RecognitionResult result, Action showDefinition)
    {
        if (result.Frame.DesktopBounds is null || result.Frame.SourceKey != source.Key)
            throw new InvalidOperationException("请重新识别当前窗口，再开启实时选词。");
        this.vm = vm; this.source = source; this.result = result; this.showDefinition = showDefinition;
        generation = vm.Scheduler.Generation;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = null;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Content = canvas; // No screenshot or transparent hit-test surface over the game's empty regions.
        wordPanel.Child = new ScrollViewer { Content = words, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 180 };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000 | 0x80);
            SetWindowDisplayAffinity(handle, 0x11);
            hwnd = HwndSource.FromHwnd(handle); hwnd.AddHook(WindowMessage);
        };
        Loaded += (_, _) => { ValidateAndPosition(); if (!closed) timer.Start(); };
        canvas.SizeChanged += (_, _) => RenderLines();
        vm.RecognitionAvailable += UpdateResult; vm.FrameChanged += FrameChanged; vm.RecognitionUnavailable += Suspend;
        timer.Tick += (_, _) =>
        {
            if (GetForegroundWindow() == source.Handle && (GetAsyncKeyState(0x1B) & 0x8000) != 0) { Close(); return; }
            ValidateAndPosition();
        };
        Closed += (_, _) =>
        {
            closed = true; timer.Stop(); hwnd?.RemoveHook(WindowMessage);
            vm.RecognitionAvailable -= UpdateResult; vm.FrameChanged -= FrameChanged; vm.RecognitionUnavailable -= Suspend;
        };
    }
    private nint WindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    { if (message == 0x0021) { handled = true; return 3; } return 0; }
    private bool IsCurrent() => !closed && vm.SelectedSource?.Key == source.Key && vm.Scheduler.Generation == generation;
    private void FrameChanged()
    {
        if (!IsCurrent()) { if (!closed) Close(); return; }
        if (vm.Result is { } current) UpdateResult(current);
    }
    internal void UpdateResult(RecognitionResult next)
    {
        if (!IsCurrent()) { if (!closed) Close(); return; }
        if (next.Frame.SourceKey != source.Key || next.Frame.Timestamp < result.Frame.Timestamp) return;
        var changed = unavailable || !result.Lines.Select(l => (l.Text, l.Bounds)).SequenceEqual(next.Lines.Select(l => (l.Text, l.Bounds)))
            || result.Frame.DesktopBounds != next.Frame.DesktopBounds;
        if (popupOpen && selectedLine is { } selected)
        {
            selectedLine = next.Lines.Where(l => l.Text == selected.Text)
                .OrderBy(l => Math.Abs(l.Bounds.X - selected.Bounds.X) + Math.Abs(l.Bounds.Y - selected.Bounds.Y)).FirstOrDefault();
            if (selectedLine is null) { popupOpen = false; choiceVersion++; }
        }
        result = next; unavailable = false;
        if (changed) RenderLines();
    }
    internal void Suspend()
    {
        unavailable = true; popupOpen = false; choiceVersion++; wordPanel.Visibility = Visibility.Collapsed;
        canvas.Children.Clear(); // Failed capture must not leave old words clickable.
    }
    private void ValidateAndPosition()
    {
        if (closed) return;
        if (!IsCurrent()) { Close(); return; }
        var rect = CaptureGeometry.Read(source.Handle, result.Frame.CapturedClientOnly);
        if (rect is null) { Suspend(); return; }
        var foreground = GetForegroundWindow(); Native.GetWindowThreadProcessId(foreground, out var process);
        canvas.Visibility = foreground == source.Handle || process == Environment.ProcessId ? Visibility.Visible : Visibility.Collapsed;
        if (placed != rect)
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, 0, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, 0x0010 | 0x0004);
            placed = rect;
        }
        if (result.Frame.DesktopBounds is not { } original || original.Width != rect.Width || original.Height != rect.Height)
        { Suspend(); return; }
        if (!unavailable && canvas.Children.Count == 0) RenderLines();
    }
    internal void RenderLines()
    {
        if (closed || unavailable || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        canvas.Children.Clear(); wordPanel.Visibility = popupOpen ? Visibility.Visible : Visibility.Collapsed;
        foreach (var line in result.Lines)
        foreach (var fragment in line.Fragments ?? new[] { line })
        {
            if (!fragment.Words.Any() || fragment.Bounds.Width <= 0 || fragment.Bounds.Height <= 0) continue;
            var bounds = CaptureGeometry.Map(fragment.Bounds, result.Frame.Width, result.Frame.Height, canvas.ActualWidth, canvas.ActualHeight);
            var button = new Button { Content = "", Tag = line, Style = null, Focusable = false,
                Background = new SolidColorBrush(Color.FromArgb(16, 168, 231, 191)), BorderBrush = new SolidColorBrush(Color.FromRgb(168, 231, 191)), BorderThickness = new(1),
                Width = Math.Max(8, bounds.Width), Height = Math.Max(8, bounds.Height), ToolTip = fragment.Text, Cursor = Cursors.Hand };
            Canvas.SetLeft(button, bounds.X); Canvas.SetTop(button, bounds.Y);
            button.Click += (_, _) => ShowWords(line, bounds);
            canvas.Children.Add(button);
        }
        canvas.Children.Add(wordPanel);
        if (popupOpen && selectedLine is { } active)
            PositionWords(CaptureGeometry.Map(active.Bounds, result.Frame.Width, result.Frame.Height, canvas.ActualWidth, canvas.ActualHeight));
        var close = new Button { Content = "实时选词 · 点行后选词   × 退出", Focusable = false, FontSize = 12, Padding = new(12, 7, 12, 7), ToolTip = "游戏画面保持实时；空白处可正常操作游戏" };
        close.Click += (_, _) => Close();
        Canvas.SetRight(close, 12); Canvas.SetTop(close, 12); canvas.Children.Add(close);
    }
    private void ShowWords(RecognizedLine line, PixelRect bounds)
    {
        if (!IsCurrent() || unavailable) return;
        var clicked = result;
        var matching = clicked.Lines.FirstOrDefault(l => l.Text == line.Text && l.Bounds == line.Bounds);
        if (matching is null) return;
        popupOpen = true; selectedLine = matching; var version = ++choiceVersion; words.Children.Clear();
        foreach (var word in matching.Words.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var button = new Button { Content = word, Focusable = false, Margin = new(0, 0, 5, 5), Padding = new(10, 6, 10, 6) };
            button.Click += (_, _) => Learn(version, word);
            words.Children.Add(button);
        }
        var dismiss = new Button { Content = "收起", Focusable = false, Padding = new(10, 6, 10, 6) };
        dismiss.Click += (_, _) => { popupOpen = false; RenderLines(); }; words.Children.Add(dismiss);
        PositionWords(bounds);
    }
    private void PositionWords(PixelRect bounds)
    {
        wordPanel.Width = Math.Min(440, Math.Max(100, canvas.ActualWidth - 24));
        wordPanel.Visibility = Visibility.Visible; wordPanel.Measure(new Size(wordPanel.Width, double.PositiveInfinity));
        var height = wordPanel.DesiredSize.Height;
        var top = bounds.Y + bounds.Height + 8;
        if (top + height > canvas.ActualHeight - 12) top = bounds.Y - height - 8;
        Canvas.SetLeft(wordPanel, Math.Clamp(bounds.X, 0, Math.Max(0, canvas.ActualWidth - wordPanel.Width)));
        Canvas.SetTop(wordPanel, Math.Clamp(top, 0, Math.Max(0, canvas.ActualHeight - height)));
    }
    private void Learn(long version, string word)
    {
        if (!IsCurrent() || unavailable || !popupOpen || version != choiceVersion) return;
        var latest = result;
        var currentLine = selectedLine;
        if (currentLine is null) return;
        using var interaction = vm.HoldAutomaticPresentation();
        vm.LearnCaptured(word, SceneContext.ForLine(latest, currentLine), latest.Frame);
        popupOpen = false; RenderLines(); showDefinition();
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
