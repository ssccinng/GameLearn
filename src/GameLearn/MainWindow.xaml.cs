using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace GameLearn;

public partial class MainWindow : Window
{
    public MainViewModel Vm { get; }
    private HotkeyManager? hotkeys;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? autoItem;
    private LookupWindow? lookup;
    private RecallToast? toast;
    private FloatingToolbar? floating;
    private bool floatingMode;
    private bool startupInitialized;
    private bool closing;
    public MainWindow()
    {
        InitializeComponent(); Vm = new MainViewModel(); DataContext = Vm;
        _ = new WindowInteractionGuard(this, Vm);
        LoadSettings();
        Loaded += async (_, _) => { if (IsVisible) await FinishStartupAsync(); };
        LiveScene.LineClicked += line => Vm.SelectedLine = line;
        Vm.FrameChanged += () => LiveScene.SetLines(Vm.Result?.Lines ?? Array.Empty<RecognizedLine>());
        Vm.ShowLookup += () =>
        {
            if (closing) return;
            if (floatingMode)
            {
                using var interaction = Vm.HoldAutomaticPresentation();
                lookup?.Close();
                if (floating is null) ShowFloating();
                floating?.ShowWords();
            }
            else OpenLookup(false);
        };
        Vm.RecallAvailable += recall => { toast?.Close(); toast = new RecallToast(recall, Vm.Settings.RecallHotkey); toast.Show(); };
        Vm.AutomaticChanged += () => { if (autoItem is not null) autoItem.Checked = Vm.IsAutomatic; if (!Vm.IsAutomatic) { toast?.Close(); toast = null; } };
        SourceInitialized += (_, _) =>
        {
            hotkeys = new HotkeyManager(this);
            try { ConfigureHotkeys(Vm.Settings.CaptureHotkey, Vm.Settings.AutoHotkey, Vm.Settings.RecallHotkey); } catch (Exception e) { Vm.Status = e.Message; }
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开 GameLearn", null, (_, _) => Dispatcher.Invoke(ShowMain));
            menu.Items.Add("显示悬浮栏", null, (_, _) => Dispatcher.Invoke(ShowFloating));
            menu.Items.Add("识别当前画面", null, (_, _) => Dispatcher.InvokeAsync(async () => await Vm.RecognizeNowAsync()));
            autoItem = new Forms.ToolStripMenuItem("自动识别") { Checked = Vm.IsAutomatic };
            autoItem.Click += (_, _) => Dispatcher.Invoke(() => Vm.IsAutomatic = !Vm.IsAutomatic); menu.Items.Add(autoItem);
            menu.Items.Add("上次遇见", null, (_, _) => Dispatcher.Invoke(ShowRecall));
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
            tray = new Forms.NotifyIcon { Text = "GameLearn · 游戏英语学习", Icon = System.Drawing.SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
        };
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true; IsEnabled = false; Vm.Status = "正在停止识别并保存…";
            hotkeys?.Dispose(); tray?.Dispose(); lookup?.Close(); toast?.Close(); floating?.Close();
            await Vm.ShutdownAsync();
            // Shutdown can complete synchronously; never re-enter Close from Closing.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    public async Task StartUserInterfaceAsync()
    {
        if (Vm.Settings.PreferFloatingMode)
        {
            // Initialize tray/hotkeys without briefly showing or activating the large window.
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            // A hidden source picker must not silently select an unrelated desktop window.
            Vm.SelectedSource = null;
            ShowFloating(); await FinishStartupAsync();
        }
        else Show();
    }
    private async Task FinishStartupAsync()
    {
        if (startupInitialized) return;
        startupInitialized = true;
        if (!Environment.GetCommandLineArgs().Contains("--diagnose") && Process.GetProcessesByName("obs64").Length > 0)
            await Vm.ConnectObsAsync();
    }
    private void ShowMain()
    {
        floatingMode = false; floating?.Hide(); Vm.Settings.PreferFloatingMode = false; Vm.Settings.Save();
        Show(); WindowState = WindowState.Normal; Activate();
    }
    private void ShowFloating()
    {
        if (closing) return;
        floatingMode = true; Vm.Settings.PreferFloatingMode = true; Vm.Settings.Save();
        if (floating is null)
        {
            floating = new FloatingToolbar(Vm, Vm.RecognizeNowAsync, ShowRecall, ShowMain, () => floating?.Hide(), () => OpenLookup(true));
            floating.Closed += (_, _) => floating = null;
        }
        Hide(); floating.Show();
    }
    private void OpenLookup(bool details)
    {
        if (closing) return;
        using var interaction = Vm.HoldAutomaticPresentation();
        if (lookup is not null && lookup.IsCompact != floatingMode) lookup.Close();
        if (lookup is null)
        {
            lookup = new LookupWindow(Vm, floatingMode); lookup.Closed += (_, _) => lookup = null;
            if (floatingMode && floating?.IsVisible == true)
                lookup.Loaded += (_, _) => { if (lookup is not null && floating?.IsVisible == true) FloatingPlacement.PlaceCard(lookup, floating); };
            lookup.Show();
        }
        else { lookup.WindowState = WindowState.Normal; lookup.Show(); lookup.Activate(); }
        lookup.ShowPage(details);
    }
    private void ShowRecall()
    {
        Vm.OpenRecall();
        if (floatingMode) { if (Vm.LastRecall is not null) OpenLookup(true); }
        else { ShowMain(); Tabs.SelectedIndex = 1; }
    }
    private void Floating_Click(object sender, RoutedEventArgs e) => ShowFloating();
    private void ConfigureHotkeys(string capture, string automatic, string recall)
    {
        if (hotkeys is null) return;
        var result = hotkeys.Configure(capture, automatic, recall,
            async () => await Vm.RecognizeNowAsync(), () => Vm.IsAutomatic = !Vm.IsAutomatic, ShowRecall);
        static string Name(int id) => id switch { 1 => "即时识别", 2 => "自动开关", _ => "场景回忆" };
        Vm.HotkeyStatus = string.Join("\n", result.Select(r => r.MatchesRequest
            ? $"{Name(r.Id)}：{r.Active} · 已启用"
            : $"{Name(r.Id)}：{r.Requested} 注册失败（{r.Error}）" + (r.Active is null ? " · 尚未启用" : $" · 仍使用 {r.Active}")));
        var conflicts = result.Where(r => !r.MatchesRequest).ToArray();
        Vm.HotkeyWarning = conflicts.Length == 0 ? "" : string.Join("、", conflicts.Select(r => r.Requested))
            + " 已被占用或无法注册，请到设置修改；其他可用快捷键不受影响。";
    }
    private void LoadSettings()
    {
        var s = Vm.Settings; EngineBox.SelectedIndex = (int)s.Engine; OcrTokenBox.Password = AppSettings.Unprotect(s.OcrSecret); AiTokenBox.Password = AppSettings.Unprotect(s.AiSecret);
        LocalIntervalBox.Text = s.LocalIntervalSeconds.ToString(CultureInfo.InvariantCulture); CloudIntervalBox.Text = s.CloudIntervalSeconds.ToString(CultureInfo.InvariantCulture); TimeoutBox.Text = s.CloudTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        CaptureKeyBox.Text = s.CaptureHotkey; AutoKeyBox.Text = s.AutoHotkey; RecallKeyBox.Text = s.RecallHotkey; AiUrlBox.Text = s.AiBaseUrl; AiModelBox.Text = s.AiModel;
        ObsLocalConfigBox.IsChecked = s.ObsUseLocalConfiguration; ObsPortBox.Text = s.ObsPort.ToString(CultureInfo.InvariantCulture); ObsPasswordBox.Password = AppSettings.Unprotect(s.ObsSecret);
    }
    private bool SaveSettings()
    {
        try
        {
            if (!double.TryParse(LocalIntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var local) || !double.IsFinite(local) || local < 0.5 || local > 300) throw new ArgumentException("本地间隔须为 0.5–300 秒。");
            if (!double.TryParse(CloudIntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cloud) || !double.IsFinite(cloud) || cloud < 5 || cloud > 600) throw new ArgumentException("在线间隔须为 5–600 秒。");
            if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 5 || timeout > 600) throw new ArgumentException("在线超时须为 5–600 秒。");
            if (!int.TryParse(ObsPortBox.Text, out var obsPort) || obsPort < 1 || obsPort > 65535) throw new ArgumentException("OBS 端口须为 1–65535。");
            AiExplanationService.ValidateConfiguration(AiUrlBox.Text, AiModelBox.Text, allowDisabled: true);
            ConfigureHotkeys(CaptureKeyBox.Text.Trim(), AutoKeyBox.Text.Trim(), RecallKeyBox.Text.Trim());
            var s = Vm.Settings; s.Engine = (OcrEngineKind)EngineBox.SelectedIndex; s.LocalIntervalSeconds = local; s.CloudIntervalSeconds = cloud; s.CloudTimeoutSeconds = timeout;
            s.CaptureHotkey = CaptureKeyBox.Text.Trim(); s.AutoHotkey = AutoKeyBox.Text.Trim(); s.RecallHotkey = RecallKeyBox.Text.Trim();
            s.OcrSecret = AppSettings.Protect(OcrTokenBox.Password.Trim()); s.AiSecret = AppSettings.Protect(AiTokenBox.Password.Trim()); s.AiBaseUrl = AiUrlBox.Text.Trim().TrimEnd('/'); s.AiModel = AiModelBox.Text.Trim();
            s.ObsUseLocalConfiguration = ObsLocalConfigBox.IsChecked == true; s.ObsPort = obsPort; s.ObsSecret = AppSettings.Protect(ObsPasswordBox.Password);
            Vm.ApplySettings(); return true;
        }
        catch (Exception e) { Vm.Status = e.Message; return false; }
    }
    private async void RefreshSources_Click(object sender, RoutedEventArgs e) { Vm.RefreshSources(); if (Vm.Sources.Any(s => s.IsObs)) await Vm.ConnectObsAsync(false); }
    private async void ConnectObs_Click(object sender, RoutedEventArgs e) => await Vm.ConnectObsAsync();
    private async void SaveConnectObs_Click(object sender, RoutedEventArgs e) { if (SaveSettings()) await Vm.ConnectObsAsync(); }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await Vm.PreviewAsync();
    private async void Recognize_Click(object sender, RoutedEventArgs e) => await Vm.RecognizeNowAsync();
    private async void Crop_Click(object sender, RoutedEventArgs e)
    {
        using var interaction = Vm.HoldAutomaticPresentation();
        if (Vm.Frame is null) await Vm.PreviewAsync();
        if (Vm.Frame is null) return;
        var dialog = new CropWindow(Vm.Frame) { Owner = this }; if (dialog.ShowDialog() == true) Vm.SetCrop(dialog.Result);
    }
    private void ClearCrop_Click(object sender, RoutedEventArgs e) => Vm.SetCrop(null);
    private void Word_Click(object sender, RoutedEventArgs e) { if (sender is Button { Content: string word }) Vm.Learn(word); }
    private void Correct_Click(object sender, RoutedEventArgs e) => Vm.Learn(Correction.Text.Trim());
    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings();
    private bool SaveAiSettings()
    {
        try { Vm.SaveAiConfiguration(AiUrlBox.Text, AiModelBox.Text, AiTokenBox.Password); return true; }
        catch (Exception error) { Vm.ReportAiConfigurationError(error.Message); return false; }
    }
    private void SaveAi_Click(object sender, RoutedEventArgs e) => SaveAiSettings();
    private async void TestAi_Click(object sender, RoutedEventArgs e) { if (SaveAiSettings()) await Vm.TestAiConnectionAsync(); }
    private async void TestCloud_Click(object sender, RoutedEventArgs e) { if (SaveSettings()) await Vm.TestCloudAsync(); }
    private void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(AppSettings.DataDirectory) { UseShellExecute = true });
    private void Cleanup_Click(object sender, RoutedEventArgs e) { Vm.Store.CleanupUnusedScenes(); Vm.Status = "未引用的场景截图已清理。"; }
    private void ClearRecords_Click(object sender, RoutedEventArgs e)
    {
        using var interaction = Vm.HoldAutomaticPresentation();
        if (MessageBox.Show("清空全部单词、遇见记录与场景截图？此操作不可撤销。", "清空学习记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Vm.ClearRecords();
    }
}
