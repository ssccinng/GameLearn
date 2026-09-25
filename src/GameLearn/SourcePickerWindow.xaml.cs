using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameLearn;

public partial class SourcePickerWindow : Window
{
    private readonly MainViewModel vm;
    public SourcePickerWindow(MainViewModel vm)
    {
        this.vm = vm; InitializeComponent();
        _ = new WindowInteractionGuard(this, vm, holdWhileVisible: true);
        vm.RefreshSources(); RefreshGroups();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => FloatingPlacement.MoveIntoView(this);
    }
    internal void RefreshGroups()
    {
        if (SearchBox is null) return;
        var query = SearchBox.Text.Trim();
        var sources = vm.Sources.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || s.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var projectors = sources.Where(s => s.IsObsProjector).ToArray();
        ProjectorItems.ItemsSource = projectors;
        NoProjectors.Visibility = projectors.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WindowItems.ItemsSource = sources.Where(s => !s.IsObs && !s.ProcessName.Equals("obs64", StringComparison.OrdinalIgnoreCase));
        ActiveItems.ItemsSource = sources.Where(s => s.Kind == CaptureSourceKind.ObsInput && s.IsActive);
        OtherItems.ItemsSource = sources.Where(s => s.Kind == CaptureSourceKind.ObsInput && !s.IsActive);
        SceneItems.ItemsSource = sources.Where(s => s.Kind is CaptureSourceKind.ObsScene or CaptureSourceKind.ObsProgram);
        InterfaceItems.ItemsSource = sources.Where(s => !s.IsObs && !s.IsObsProjector && s.ProcessName.Equals("obs64", StringComparison.OrdinalIgnoreCase));
        OtherGroup.IsExpanded = SceneGroup.IsExpanded = InterfaceGroup.IsExpanded = query.Length > 0;
        CurrentSource.Text = "当前：" + (vm.SelectedSource?.Title ?? "尚未选择");
        PickerStatus.Text = $"{sources.Length} 个匹配来源 · 搜索可展开隐藏来源";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized) RefreshGroups(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) { vm.RefreshSources(); RefreshGroups(); }
    private async void Obs_Click(object sender, RoutedEventArgs e)
    {
        ObsButton.IsEnabled = RefreshButton.IsEnabled = false;
        try { await vm.ConnectObsAsync(false); if (!IsLoaded) return; RefreshGroups(); PickerStatus.Text = vm.ObsStatus; }
        finally { ObsButton.IsEnabled = RefreshButton.IsEnabled = true; }
    }
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowSource source }) { vm.SelectedSource = source; Close(); }
    }
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
