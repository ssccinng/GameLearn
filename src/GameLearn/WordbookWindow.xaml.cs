using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameLearn;

public partial class WordbookWindow : Window
{
    private readonly MainViewModel vm;
    private readonly Action toolbar;
    public bool IsCompact { get; private set; }
    public WordbookWindow(MainViewModel vm, bool compact, Action toolbar)
    {
        this.vm = vm; this.toolbar = toolbar; InitializeComponent(); DataContext = vm;
        SearchBox.Text = vm.Search;
        _ = new WindowInteractionGuard(this, vm, holdWhileVisible: true);
        SetCompact(compact); RefreshRecent();
        Loaded += (_, _) => FloatingPlacement.MoveIntoView(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    public void ShowRecent() { RefreshRecent(); Pages.SelectedIndex = 1; }
    internal void SetCompact(bool compact)
    {
        IsCompact = compact; Topmost = compact; Width = compact ? 480 : 900;
        ListColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(250);
        DetailColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        WideDetail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactDetail.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (!compact && Pages.SelectedIndex == 2) Pages.SelectedIndex = 0;
        CompactButton.Content = compact ? "展开" : "紧凑悬浮";
        if (IsLoaded) { UpdateLayout(); FloatingPlacement.MoveIntoView(this); }
    }
    private void RefreshRecent()
    {
        var items = vm.Store.RecentRecognitions(SearchBox.Text.Trim()); RecentItems.ItemsSource = items;
        Summary.Text = items.Count == 0 ? "暂无匹配的历史原句 · 从本版开始保留识别记录" : $"{items.Count} 条历史识别 · 仅查过的词加入单词本 · 滚轮浏览";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        vm.Search = SearchBox.Text; RefreshRecent();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { vm.RefreshWords(); RefreshRecent(); }
    private void Word_Selected(object sender, SelectionChangedEventArgs e)
    { if (IsCompact && Pages.SelectedIndex == 0 && SavedWords.IsKeyboardFocusWithin && SavedWords.SelectedItem is not null) Pages.SelectedIndex = 2; }
    private void Word_Clicked(object sender, MouseButtonEventArgs e)
    {
        if (IsCompact && ItemsControl.ContainerFromElement(SavedWords, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: SavedWord word })
        { vm.SelectedWord = word; Pages.SelectedIndex = 2; }
    }
    private void Compact_Click(object sender, RoutedEventArgs e) => SetCompact(!IsCompact);
    private void Toolbar_Click(object sender, RoutedEventArgs e) { Close(); toolbar(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
