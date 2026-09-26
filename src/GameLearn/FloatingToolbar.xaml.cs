using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace GameLearn;

public partial class FloatingToolbar : Window
{
    private readonly MainViewModel vm;
    private readonly Func<Task> recognize;
    private readonly Action recall;
    private readonly Action expand;
    private readonly Action? showWordbook;
    private readonly Func<bool, bool>? setSelectionMode;
    private readonly Action? chooseSource;
    private readonly Action? showRecent;
    private readonly Action hide;
    private readonly Action? showDefinition;
    private IDisposable? dragging;
    private HwndSource? source;
    private bool moved;
    public bool IsCollapsed { get; private set; }

    public FloatingToolbar(MainViewModel vm, Func<Task> recognize, Action recall, Action expand, Action hide, Action? showDefinition = null, Action? showWordbook = null, Func<bool, bool>? setSelectionMode = null, Action? chooseSource = null, Action? showRecent = null)
    {
        InitializeComponent(); this.vm = vm; this.recognize = recognize; this.recall = recall; this.expand = expand; this.hide = hide; this.showWordbook = showWordbook; this.setSelectionMode = setSelectionMode;
        this.showDefinition = showDefinition;
        this.chooseSource = chooseSource;
        this.showRecent = showRecent;
        DataContext = vm; _ = new WindowInteractionGuard(this, vm);
        vm.FrameChanged += UpdateVisibleWords;
        UpdateVisibleWords();
        SetCollapsed(vm.Settings.FloatingBarCollapsed, false);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000 | 0x80);
            source = HwndSource.FromHwnd(handle); source.AddHook(WindowMessage);
        };
        Loaded += (_, _) => RestorePosition();
        IsVisibleChanged += (_, _) => { if (IsVisible) ToolbarMotion.Enter(IsCollapsed ? CollapsedContent : ExpandedContent); };
        Closed += (_, _) => { vm.FrameChanged -= UpdateVisibleWords; dragging?.Dispose(); dragging = null; source?.RemoveHook(WindowMessage); };
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE: toggling/dragging does not steal game focus.
        return 0;
    }
    internal void SetCollapsed(bool collapsed, bool save = true)
    {
        var previousBounds = IsLoaded ? FloatingPlacement.WindowBounds(this) : null;
        IsCollapsed = collapsed;
        ExpandedContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedContent.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        Width = collapsed ? 64 : 680; Height = collapsed ? 64 : 460;
        if (IsVisible) ToolbarMotion.Enter(collapsed ? CollapsedContent : ExpandedContent);
        if (!save) return;
        vm.Settings.FloatingBarCollapsed = collapsed;
        if (IsLoaded)
        {
            UpdateLayout(); var resized = FloatingPlacement.WindowBounds(this);
            FloatingPlacement.MoveIntoView(this, previousBounds is null ? null : previousBounds.X + previousBounds.Width - resized.Width, previousBounds?.Y);
            SavePosition();
        }
        else vm.Settings.Save();
    }
    private void UpdateVisibleWords()
    {
        var lines = vm.GetPrioritizedLines().Where(line => line.Words.Count > 0).ToArray();
        CurrentWordLines.ItemsSource = lines;
        EmptyWords.Visibility = lines.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyWordsHint.Text = vm.Result is null ? "点击「识别」，或开启「自动」持续读取游戏画面。"
            : "这次没有识别到英文。可以在主界面框选对话区域后再识别。";
        SceneStamp.Text = vm.Result is null ? "尚未识别" : $"{lines.Length} 段 · {lines.Sum(line => line.Words.Count)} 个词 · 难词优先";
        WordsScroll.ScrollToTop();
    }
    public void ShowWords()
    {
        if (IsCollapsed) SetCollapsed(false);
        Show();
    }
    private void Word_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string word, Tag: RecognizedLine line }) return;
        word = System.Text.RegularExpressions.Regex.Match(word, @"[A-Za-z]+(?:['’\-][A-Za-z]+)*").Value;
        if (word.Length == 0) return;
        if (!vm.Lines.Any(current => ReferenceEquals(current, line))) return;
        using var interaction = vm.HoldAutomaticPresentation();
        vm.SelectedLine = line; vm.Learn(word);
        if (vm.SelectedWord is not null) showDefinition?.Invoke();
        e.Handled = true;
    }
    private void RestorePosition()
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var bounds = FloatingPlacement.WindowBounds(this);
        FloatingPlacement.MoveIntoView(this, vm.Settings.FloatingBarX ?? work.Right - bounds.Width - 24,
            vm.Settings.FloatingBarY ?? work.Bottom - bounds.Height - 24);
    }
    private void SavePosition()
    {
        var bounds = FloatingPlacement.WindowBounds(this);
        vm.Settings.FloatingBarX = bounds.X; vm.Settings.FloatingBarY = bounds.Y;
        vm.Settings.Save();
    }
    private void Drag_Started(object sender, DragStartedEventArgs e) { moved = false; dragging = vm.HoldAutomaticPresentation(); }
    private void Drag_Delta(object sender, DragDeltaEventArgs e)
    {
        moved |= Math.Abs(e.HorizontalChange) + Math.Abs(e.VerticalChange) > 0.5;
        Left += e.HorizontalChange; Top += e.VerticalChange;
    }
    private void Drag_Completed(object sender, DragCompletedEventArgs e)
    {
        FloatingPlacement.MoveIntoView(this); SavePosition(); dragging?.Dispose(); dragging = null;
        if (IsCollapsed && !moved) SetCollapsed(false);
    }
    private async void Capture_Click(object sender, RoutedEventArgs e) => await recognize();
    private async void Automatic_Click(object sender, RoutedEventArgs e)
    {
        // Keep the toggle usable even when the no-activate window prevents WPF from
        // updating the binding before the click event arrives.
        var requested = AutomaticSwitch.IsChecked == true;
        if (requested != vm.IsAutomatic) vm.IsAutomatic = requested;
        // Enabling automatic mode is an explicit user action. Seed an empty panel once,
        // even while the pointer remains over the switch and background presentation is held.
        if (vm.IsAutomatic && vm.Result is null) await recognize();
    }
    private void Recall_Click(object sender, RoutedEventArgs e) => recall();
    private void Expand_Click(object sender, RoutedEventArgs e) => expand();
    private void Wordbook_Click(object sender, RoutedEventArgs e) => (showWordbook ?? expand)();
    private void Source_Click(object sender, RoutedEventArgs e) => (chooseSource ?? expand)();
    private void Recent_Click(object sender, RoutedEventArgs e) => (showRecent ?? showWordbook ?? expand)();
    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        var requested = SelectionSwitch.IsChecked == true;
        if (setSelectionMode is null || setSelectionMode(requested)) return;
        SelectionSwitch.IsChecked = false;
    }
    internal void SetSelectionMode(bool enabled) => SelectionSwitch.IsChecked = enabled;
    private void Collapse_Click(object sender, RoutedEventArgs e) => SetCollapsed(!IsCollapsed);
    private void Hide_Click(object sender, RoutedEventArgs e) => hide();
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
