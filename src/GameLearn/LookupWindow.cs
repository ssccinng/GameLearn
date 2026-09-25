using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

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
        _ = new WindowInteractionGuard(this, vm);
        Width = 1120; Height = 780; MinWidth = 850; MinHeight = 620; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (compact) { Width = 460; Height = 680; MinWidth = 380; MinHeight = 420; ShowInTaskbar = false; Title = "GameLearn · 游戏取词 / Esc 收起"; }
        var grid = new Grid { Margin = new Thickness(22) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(335) });
        var left = new Grid { Margin = new Thickness(0, 0, 20, 0) }; left.RowDefinitions.Add(new RowDefinition()); left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        scene.SetBinding(SceneViewer.SourceProperty, new Binding(nameof(MainViewModel.Preview))); scene.LineClicked += line => vm.SelectedLine = line; left.Children.Add(scene);
        var lines = new ListBox { DisplayMemberPath = "Text" }; lines.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.Lines))); lines.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedLine)) { Mode = BindingMode.TwoWay }); Grid.SetRow(lines, 1); left.Children.Add(lines);
        var tokens = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        void RefreshTokens()
        {
            tokens.Children.Clear(); foreach (var word in vm.LineWords) { var b = new Button { Content = word, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 7, 10, 7) }; b.Click += (_, _) => { vm.Learn(word); ShowPage(true); }; tokens.Children.Add(b); }
        }
        var tokensScroll = new ScrollViewer { Content = tokens, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(tokensScroll, 2); left.Children.Add(tokensScroll);
        var correction = new TextBox { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0) };
        var correctButton = new Button { Content = "修正并查词" }; correctButton.Click += (_, _) => { vm.Learn(correction.Text.Trim()); if (vm.SelectedWord is not null) ShowPage(true); };
        var correctPanel = new StackPanel { Orientation = Orientation.Horizontal }; correctPanel.Children.Add(correction); correctPanel.Children.Add(correctButton); Grid.SetRow(correctPanel, 3); left.Children.Add(correctPanel);
        grid.Children.Add(left);
        var detail = new Border { Child = new WordDetailView(), Style = (Style)FindResource("Card"), Padding = new Thickness(18) }; Grid.SetColumn(detail, 1); grid.Children.Add(detail); Content = grid;
        if (compact)
        {
            grid.Children.Clear(); left.Margin = new Thickness(0);
            compactTabs = new TabControl { Margin = new Thickness(16) };
            compactTabs.Items.Add(new TabItem { Header = "当前画面 · 点词", Content = left });
            compactTabs.Items.Add(new TabItem { Header = "释义与场景", Content = detail });
            Content = compactTabs;
        }
        void UpdateFrame() => scene.SetLines(vm.Result?.Lines ?? Array.Empty<RecognizedLine>());
        System.Collections.Specialized.NotifyCollectionChangedEventHandler tokenHandler = (_, _) => RefreshTokens();
        vm.LineWords.CollectionChanged += tokenHandler; vm.FrameChanged += UpdateFrame;
        Closed += (_, _) => { vm.LineWords.CollectionChanged -= tokenHandler; vm.FrameChanged -= UpdateFrame; vm.LookupIsOpen = false; };
        void UpdateLookupHold() => vm.LookupIsOpen = IsVisible && WindowState != WindowState.Minimized;
        IsVisibleChanged += (_, _) => UpdateLookupHold(); StateChanged += (_, _) => UpdateLookupHold();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        UpdateFrame(); RefreshTokens();
    }
    public void ShowPage(bool details) { if (compactTabs is not null) compactTabs.SelectedIndex = details ? 1 : 0; }
}

