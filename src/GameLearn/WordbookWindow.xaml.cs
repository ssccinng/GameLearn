using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;

namespace GameLearn;

public partial class WordbookWindow : Window
{
    private readonly MainViewModel vm;
    private readonly Action toolbar;
    public bool IsCompact { get; private set; }
    private bool syncingFilters;
    public WordbookWindow(MainViewModel vm, bool compact, Action toolbar)
    {
        this.vm = vm; this.toolbar = toolbar; InitializeComponent(); DataContext = vm;
        SearchBox.Text = vm.Search;
        _ = new WindowInteractionGuard(this, vm, holdWhileVisible: true);
        SetCompact(compact); RefreshRecent();
        SyncFilters(); vm.PropertyChanged += VmChanged;
        Closed += (_, _) => vm.PropertyChanged -= VmChanged;
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
        Filters.IsExpanded = !compact;
        if (IsLoaded) { UpdateLayout(); FloatingPlacement.MoveIntoView(this); }
    }
    private void RefreshRecent()
    {
        var items = vm.Store.RecentRecognitions(SearchBox.Text.Trim()); RecentItems.ItemsSource = items;
        Summary.Text = Pages.SelectedIndex == 1
            ? items.Count == 0 ? "暂无匹配的历史原句" : $"{items.Count} 条历史识别 · 滚轮浏览"
            : vm.FilteredWordCount;
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || syncingFilters) return;
        vm.Search = SearchBox.Text; RefreshRecent();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { vm.RefreshWords(); RefreshRecent(); }
    private void RecentWord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentWord recent }) return;
        var frame = recent.Recent.ToFrame();
        if (frame is null) { vm.Status = "这条旧历史没有保存截图，无法回到原场景；新识别会支持点词。"; return; }
        vm.SelectedLine = recent.Line; vm.LearnCaptured(recent.Word, recent.Line, frame); Pages.SelectedIndex = IsCompact ? 2 : 0;
    }
    private void Word_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (Pages.SelectedIndex == 0 && SavedWords.IsKeyboardFocusWithin && Mouse.LeftButton != MouseButtonState.Pressed
            && e.AddedItems.Count > 0 && e.AddedItems[0] is SavedWord word && vm.ViewWord(word) && IsCompact) Pages.SelectedIndex = 2;
    }
    private void Word_Clicked(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SavedWords, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: SavedWord word })
        { vm.ViewWord(word); if (IsCompact) Pages.SelectedIndex = 2; }
    }
    private void VmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(vm.FilteredWordCount) && Pages.SelectedIndex != 1) Summary.Text = vm.FilteredWordCount;
        if (!syncingFilters && e.PropertyName is nameof(vm.MasteredFilter) or nameof(vm.WordSort) or nameof(vm.WordDateFrom) or nameof(vm.WordDateThrough) or nameof(vm.Search)) SyncFilters();
    }
    private void SyncFilters()
    {
        syncingFilters = true;
        try
        {
            StatusBox.SelectedIndex = vm.MasteredFilter is null ? 0 : vm.MasteredFilter.Value ? 2 : 1;
            SortBox.SelectedIndex = (int)vm.WordSort; SearchBox.Text = vm.Search;
            DateBox.SelectedIndex = vm.WordDateFrom is null && vm.WordDateThrough is null ? 0
                : vm.WordDateThrough == DateTime.Today && vm.WordDateFrom == DateTime.Today ? 1
                : vm.WordDateThrough == DateTime.Today && vm.WordDateFrom == DateTime.Today.AddDays(-6) ? 2
                : vm.WordDateThrough == DateTime.Today && vm.WordDateFrom == DateTime.Today.AddDays(-29) ? 3 : 4;
            FromDate.Text = vm.WordDateFrom?.ToString("yyyy-MM-dd") ?? "";
            ThroughDate.Text = vm.WordDateThrough?.ToString("yyyy-MM-dd") ?? "";
            CustomDates.Visibility = DateBox.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            FilterHint.Text = "日期按首次遇见筛选；分类在词条详情中编辑。";
        }
        finally { syncingFilters = false; }
    }
    private void Status_Changed(object sender, SelectionChangedEventArgs e)
    { if (IsInitialized && !syncingFilters) vm.MasteredFilter = StatusBox.SelectedIndex == 0 ? null : StatusBox.SelectedIndex == 2; }
    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    { if (IsInitialized && !syncingFilters) vm.WordSort = (WordbookSort)SortBox.SelectedIndex; }
    private void Date_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || syncingFilters) return;
        CustomDates.Visibility = DateBox.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (DateBox.SelectedIndex == 4) { ApplyCustomDates(); return; }
        syncingFilters = true;
        try
        {
            vm.WordDateFrom = DateBox.SelectedIndex switch { 1 => DateTime.Today, 2 => DateTime.Today.AddDays(-6), 3 => DateTime.Today.AddDays(-29), _ => null };
            vm.WordDateThrough = DateBox.SelectedIndex == 0 ? null : DateTime.Today;
            FilterHint.Text = "日期按首次遇见筛选；分类在词条详情中编辑。";
        }
        finally { syncingFilters = false; }
    }
    private void CustomDate_Changed(object sender, TextChangedEventArgs e)
    { if (IsInitialized && !syncingFilters && DateBox.SelectedIndex == 4) ApplyCustomDates(); }
    private void ApplyCustomDates()
    {
        static bool Read(string text, out DateTime? date)
        {
            date = null; if (string.IsNullOrWhiteSpace(text)) return true;
            if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
            date = parsed; return true;
        }
        if (!Read(FromDate.Text, out var from) || !Read(ThroughDate.Text, out var through) || (from is not null && through is not null && from > through))
        { FilterHint.Text = "请输入 yyyy-MM-dd，起始日期不能晚于截止日期。"; return; }
        syncingFilters = true;
        try { vm.WordDateFrom = from; vm.WordDateThrough = through; FilterHint.Text = "自定义范围包含起止当天；留空的一端不限。"; }
        finally { syncingFilters = false; }
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { vm.ResetWordFilters(); SyncFilters(); RefreshRecent(); }
    private void Page_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !ReferenceEquals(e.Source, Pages)) return;
        Filters.Visibility = Pages.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshRecent();
    }
    private void Compact_Click(object sender, RoutedEventArgs e) => SetCompact(!IsCompact);
    private void Toolbar_Click(object sender, RoutedEventArgs e) { Close(); toolbar(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
