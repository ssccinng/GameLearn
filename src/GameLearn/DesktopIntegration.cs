using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GameLearn;

public sealed class HotkeyManager : IDisposable
{
    private readonly nint handle;
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> actions = new();
    private readonly HotkeyRegistrationSet registrations;
    private bool disposed;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
    public HotkeyManager(Window window)
    {
        handle = new WindowInteropHelper(window).Handle; source = HwndSource.FromHwnd(handle);
        registrations = new(new WindowRegistrar(handle)); source.AddHook(Hook);
    }
    public static (uint Modifiers, uint Key) Parse(string text)
    {
        uint modifiers = 0x4000; var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("快捷键至少包含 Ctrl、Alt 或 Shift 中的一个修饰键，如 Ctrl+Alt+E。");
        foreach (var part in parts[..^1])
            modifiers |= part.ToUpperInvariant() switch { "CTRL" => 2u, "ALT" => 1u, "SHIFT" => 4u, "WIN" => 8u, _ => throw new ArgumentException("未知快捷键修饰符：" + part) };
        if (!Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None) throw new ArgumentException("快捷键按键无效：" + parts[^1]);
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    public IReadOnlyList<HotkeyRegistration> Configure(string capture, string automatic, string recall, Action captureAction, Action autoAction, Action recallAction)
    {
        var candidate = new Dictionary<int, string> { [1] = capture, [2] = automatic, [3] = recall };
        var result = registrations.Apply(candidate);
        actions[1] = captureAction; actions[2] = autoAction; actions[3] = recallAction;
        return result;
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!disposed && message == 0x0312 && registrations.Active.ContainsKey((int)wParam) && actions.TryGetValue((int)wParam, out var action))
        {
            handled = true;
            // Native window messages need not carry WPF's synchronization context.
            // Dispatch before starting async capture so continuations stay on the UI thread.
            source.Dispatcher.BeginInvoke(() => { if (!disposed) action(); });
        }
        return 0;
    }
    private sealed class WindowRegistrar(nint handle) : IHotkeyRegistrar
    {
        public int Register(int id, uint modifiers, uint key) => RegisterHotKey(handle, id, modifiers, key) ? 0 : Marshal.GetLastWin32Error();
        public void Unregister(int id) => UnregisterHotKey(handle, id);
    }
    public void Dispose() { disposed = true; registrations.Dispose(); source.RemoveHook(Hook); }
}

public sealed class CropWindow : Window
{
    private readonly SceneViewer viewer;
    private System.Windows.Point? start;
    private Rectangle? selection;
    public CropRegion? Result { get; private set; }
    public CropWindow(CapturedFrame frame)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "框选对话区域 · 在截图上拖动鼠标"; Width = 1120; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var top = new TextBlock { Text = "拖动选择对话区域，松开后确认。只识别框内文字，保存时仍保留完整场景。", Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom);
        var confirm = new Button { Content = "使用这个区域", Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("Primary") };
        confirm.Click += (_, _) => { if (Result is not null) DialogResult = true; };
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => DialogResult = false;
        bottom.Children.Add(cancel); bottom.Children.Add(confirm); panel.Children.Add(bottom);
        viewer = new SceneViewer { Source = CapturedFrame.Image(frame.FullPng) }; panel.Children.Add(viewer); Content = panel;
        viewer.Overlay.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(viewer.Overlay); viewer.Overlay.Children.Clear();
            selection = new Rectangle { Stroke = Brushes.LightGreen, StrokeThickness = 4, Fill = new SolidColorBrush(Color.FromArgb(55, 168, 231, 191)) };
            viewer.Overlay.Children.Add(selection); viewer.Overlay.CaptureMouse();
        };
        viewer.Overlay.MouseMove += (_, e) =>
        {
            if (start is not { } first || selection is null) return;
            var last = e.GetPosition(viewer.Overlay);
            var left = Math.Clamp(Math.Min(first.X, last.X), 0, frame.Width); var topY = Math.Clamp(Math.Min(first.Y, last.Y), 0, frame.Height);
            var right = Math.Clamp(Math.Max(first.X, last.X), 0, frame.Width); var bottomY = Math.Clamp(Math.Max(first.Y, last.Y), 0, frame.Height);
            Canvas.SetLeft(selection, left); Canvas.SetTop(selection, topY); selection.Width = right - left; selection.Height = bottomY - topY;
            Result = selection.Width >= 8 && selection.Height >= 8 ? new(left / frame.Width, topY / frame.Height, selection.Width / frame.Width, selection.Height / frame.Height) : null;
        };
        viewer.Overlay.MouseLeftButtonUp += (_, _) => { start = null; viewer.Overlay.ReleaseMouseCapture(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}

public sealed class RecallToast : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    public RecallToast(Recall recall, string hotkey)
    {
        Width = 330; Height = 110; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false; IsHitTestVisible = false;
        var stack = new StackPanel(); stack.Children.Add(new TextBlock { Text = $"这个词见过：{recall.Word.Word}", FontSize = 19, Foreground = new SolidColorBrush(Color.FromRgb(168, 231, 191)) });
        stack.Children.Add(new TextBlock { Text = $"{hotkey}  回到上次的场景", Margin = new Thickness(0, 10, 0, 0), FontSize = 13, Foreground = Brushes.LightGray });
        Content = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(22, 35, 45)), Padding = new Thickness(20), Child = stack };
        Left = SystemParameters.WorkArea.Right - Width - 24; Top = SystemParameters.WorkArea.Bottom - Height - 24;
        SourceInitialized += (_, _) => { var h = new WindowInteropHelper(this).Handle; SetWindowLongPtr(h, -20, GetWindowLongPtr(h, -20) | 0x08000000 | 0x20 | 0x80); };
        timer.Tick += (_, _) => Close(); Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop();
    }
}

