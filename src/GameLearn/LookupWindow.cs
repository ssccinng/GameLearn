using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GameLearn;

public sealed class LookupWindow : Window
{
    private readonly SceneViewer scene = new();
    private readonly MainViewModel vm;
    private readonly TabControl? compactTabs;
    public bool IsCompact { get; }
    public LookupWindow(MainViewModel vm, bool compact = false)
    {
        IsCompact = compact;
        this.vm = vm; DataContext = vm; Style = (Style)FindResource(typeof(Window)); Title = "GameLearn · 点词查释义 / Esc 返回游戏";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        _ = new WindowInteractionGuard(this, vm);
        Width = 1120; Height = 780; MinWidth = 850; MinHeight = 620; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (compact) { Width = 460; Height = 680; MinWidth = 380; MinHeight = 420; ShowInTaskbar = false; Title = "GameLearn · 游戏取词 / Esc 收起"; }
        var grid = new Grid { Margin = new Thickness(22) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(335) });
        var left = new Grid { Margin = new Thickness(0, 0, 20, 0) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition());
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sceneHeader = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        sceneHeader.Children.Add(new TextBlock { Text = "当前截图", FontSize = 15, FontWeight = FontWeights.SemiBold });
        sceneHeader.Children.Add(new TextBlock { Text = "点文字框选择原句", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
        left.Children.Add(sceneHeader);
        scene.SetBinding(SceneViewer.SourceProperty, new Binding(nameof(MainViewModel.Preview))); scene.LineClicked += line => vm.SelectedLine = line;
        var sceneFrame = new Border { Background = new SolidColorBrush(Color.FromRgb(9, 15, 20)), BorderBrush = new SolidColorBrush(Color.FromRgb(40, 61, 68)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = scene };
        Grid.SetRow(sceneFrame, 1); left.Children.Add(sceneFrame);
        var linesHeader = new TextBlock { Text = "识别到的句子", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11, Margin = new Thickness(2, 10, 0, 2) };
        Grid.SetRow(linesHeader, 2); left.Children.Add(linesHeader);
        var lines = new ListBox { DisplayMemberPath = "Text", Margin = new Thickness(0, 23, 0, 0) }; lines.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden); lines.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.Lines))); lines.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedLine)) { Mode = BindingMode.TwoWay }); Grid.SetRow(lines, 2); left.Children.Add(lines);
        var tokens = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        void RefreshTokens()
        {
            tokens.Children.Clear(); foreach (var word in vm.LineWords) { var b = new Button { Content = word, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 7, 10, 7) }; b.Click += (_, _) => { vm.Learn(word); ShowPage(true); }; tokens.Children.Add(b); }
        }
        var tokensHeader = new TextBlock { Text = "点击词语查看释义", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11, Margin = new Thickness(2, 10, 0, 0) };
        Grid.SetRow(tokensHeader, 3); left.Children.Add(tokensHeader);
        var tokensScroll = new ScrollViewer { Content = tokens, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Margin = new Thickness(0, 22, 0, 0) }; Grid.SetRow(tokensScroll, 3); left.Children.Add(tokensScroll);
        var correction = new TextBox { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0) };
        var correctButton = new Button { Content = "修正并查词" }; correctButton.Click += (_, _) => { vm.Learn(correction.Text.Trim()); if (vm.SelectedWord is not null) ShowPage(true); };
        var correctPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) }; correctPanel.Children.Add(correction); correctPanel.Children.Add(correctButton); Grid.SetRow(correctPanel, 4); left.Children.Add(correctPanel);
        grid.Children.Add(left);
        var detail = new Border { Child = new WordDetailView(), Style = (Style)FindResource("Card"), Padding = new Thickness(18) }; Grid.SetColumn(detail, 1); grid.Children.Add(detail);
        FrameworkElement contentView = grid;
        if (compact)
        {
            grid.Children.Clear(); left.Margin = new Thickness(0);
            compactTabs = new TabControl { Margin = new Thickness(16) };
            compactTabs.Items.Add(new TabItem { Header = "当前画面", Content = left });
            compactTabs.Items.Add(new TabItem { Header = "单词详情", Content = detail });
            contentView = compactTabs;
        }
        Content = CreateSurface(contentView, compact);
        void UpdateFrame() => scene.SetLines(vm.Result?.Lines ?? Array.Empty<RecognizedLine>());
        System.Collections.Specialized.NotifyCollectionChangedEventHandler tokenHandler = (_, _) => RefreshTokens();
        vm.LineWords.CollectionChanged += tokenHandler; vm.FrameChanged += UpdateFrame;
        Closed += (_, _) => { vm.LineWords.CollectionChanged -= tokenHandler; vm.FrameChanged -= UpdateFrame; vm.LookupIsOpen = false; };
        void UpdateLookupHold() => vm.LookupIsOpen = IsVisible && WindowState != WindowState.Minimized;
        IsVisibleChanged += (_, _) => UpdateLookupHold(); StateChanged += (_, _) => UpdateLookupHold();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        UpdateFrame(); RefreshTokens();
    }
    private Border CreateSurface(FrameworkElement content, bool compact)
    {
        var header = new Grid { Height = 42, Margin = new Thickness(18, 10, 12, 0), Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = compact ? "游戏取词" : "点词查释义", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(236, 243, 245)) });
        title.Children.Add(new TextBlock { Text = compact ? "从当前画面选择一个词" : "查看词义、语境与遇见记录", FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(title);
        var close = new Button { Content = "×", Width = 32, Height = 30, Padding = new Thickness(0), FontSize = 17, ToolTip = "关闭" };
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch (InvalidOperationException) { } } };
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(header); Grid.SetRow(header, 0);
        var contentHost = new Border { Background = Brushes.Transparent, Child = content, Margin = new Thickness(0, 0, 0, 4) }; Grid.SetRow(contentHost, 1); body.Children.Add(contentHost);
        return new Border { Background = new SolidColorBrush(Color.FromRgb(17, 30, 38)), BorderBrush = new SolidColorBrush(Color.FromRgb(64, 86, 91)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = body };
    }
    public void ShowPage(bool details) { if (compactTabs is not null) compactTabs.SelectedIndex = details ? 1 : 0; }
}

