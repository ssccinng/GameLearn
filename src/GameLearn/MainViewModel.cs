using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameLearn;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public LearningStore Store { get; } = new();
    public OfflineDictionary Dictionary { get; } = new();
    public RecognitionScheduler Scheduler { get; } = new();
    private readonly CaptureService capture = new();
    private readonly LocalOcrProvider local = new();
    private readonly ObsCaptureService obs;
    private IReadOnlyList<WindowSource> obsSources = Array.Empty<WindowSource>();
    private bool connectingObs;
    private bool refreshingSources;
    private Task obsReset = Task.CompletedTask;
    private readonly HttpClient http;
    private readonly EncounterTracker tracker;
    private readonly DispatcherTimer timer;
    private CancellationTokenSource sourceCancellation = new();
    private CancellationTokenSource aiConfigurationCancellation = new();
    private long aiConfigurationVersion;
    private string aiConfigurationStatus = "填写配置后，点击「保存并测试 AI」验证服务。";
    private bool testingAi;
    public string AiConfigurationStatus { get => aiConfigurationStatus; private set => Set(ref aiConfigurationStatus, value); }
    public bool IsAiTesting { get => testingAi; private set { if (Set(ref testingAi, value)) Raise(nameof(CanTestAi)); } }
    public bool CanTestAi => !IsAiTesting;
    private CancellationTokenSource? automaticCapture;
    private DateTimeOffset nextAutomatic;
    private long manualSequence;
    private bool disposed;
    private bool refreshingWords;
    private int captureOperations;
    private Task captureStop = Task.CompletedTask;
    private readonly PresentationGate presentation = new();
    private readonly object lookupInteraction = new();
    private (RecognitionRequest Request, RecognitionResult Result)? deferredAutomatic;
    private string? deferredAutomaticStatus;
    public bool IsAutomaticRefreshPaused => presentation.IsHeld;
    public string LiveRefreshHint => IsAutomatic && IsAutomaticRefreshPaused ? "操作中 · 已固定画面与列表" : "点击文字框，选择要学习的词";
    public void SetPresentationInteraction(object owner, bool active) => presentation.SetHeld(owner, active);
    public IDisposable HoldAutomaticPresentation() => presentation.Hold();
    public ObservableCollection<WindowSource> Sources { get; } = new();
    public ObservableCollection<RecognizedLine> Lines { get; } = new();
    public ObservableCollection<string> LineWords { get; } = new();
    public ObservableCollection<SavedWord> Words { get; } = new();
    public ObservableCollection<Encounter> History { get; } = new();
    public ObservableCollection<string> Games { get; } = new() { "全部游戏" };
    public event Action? ShowLookup;
    public event Action<Recall>? RecallAvailable;
    public event Action? FrameChanged;
    public event Action? AutomaticChanged;
    private WindowSource? selectedSource;
    public WindowSource? SelectedSource
    {
        get => selectedSource;
        set
        {
            if (refreshingSources && value is null) return;
            var oldKey = selectedSource?.Key;
            if (!Set(ref selectedSource, value)) return;
            if (oldKey == value?.Key) return;
            Invalidate(); Crop = null; Result = null; Frame = null; Preview = null; Lines.Clear(); LineWords.Clear(); SelectedLine = null;
            GameName = value?.IsObs == true ? $"OBS · {value.Title}" : value?.ProcessName == "obs64" ? "OBS 游戏画面" : value?.Title ?? "";
            if (value?.IsObs == true) { Settings.PreferredObsSourceKey = value.Key; Settings.Save(); }
            Status = value is null ? "请选择画面来源" : "来源已选好 · 按快捷键识别，或开启自动模式";
            Raise(nameof(RegionLabel)); FrameChanged?.Invoke();
        }
    }
    private string gameName = "";
    public string GameName { get => gameName; set { if (Set(ref gameName, value)) Invalidate(); } }
    private string status = "准备就绪 · 自动识别关闭";
    public string Status { get => status; set => Set(ref status, value); }
    private string hotkeyStatus = "快捷键正在初始化…", hotkeyWarning = "";
    public string HotkeyStatus { get => hotkeyStatus; set => Set(ref hotkeyStatus, value); }
    public string HotkeyWarning { get => hotkeyWarning; set { if (Set(ref hotkeyWarning, value)) Raise(nameof(HasHotkeyIssues)); } }
    public bool HasHotkeyIssues => HotkeyWarning.Length != 0;
    private string obsStatus = "未连接 OBS · 可直接读取捕获源或输出画面";
    public string ObsStatus { get => obsStatus; private set => Set(ref obsStatus, value); }
    private BitmapImage? preview;
    public BitmapImage? Preview { get => preview; private set => Set(ref preview, value); }
    public CapturedFrame? Frame { get; private set; }
    public RecognitionResult? Result { get; private set; }
    public CropRegion? Crop { get; private set; }
    public string RegionLabel => Crop is null ? "识别范围：整个所选窗口" : "识别范围：已框选的对话区域";
    public string EngineLabel => Settings.Engine == OcrEngineKind.Local ? "本地 OCR · 离线" : "PP-OCRv6 · 在线";
    public bool IsAutomatic
    {
        get => Scheduler.AutoEnabled;
        set
        {
            if (value == IsAutomatic) return;
            if (value && SelectedSource is null) { Status = "请先选择画面来源。"; Raise(); return; }
            if (value && Settings.Engine == OcrEngineKind.PaddleCloud && string.IsNullOrWhiteSpace(AppSettings.Unprotect(Settings.OcrSecret)))
            { Status = "请先填写官方 OCR 令牌并保存设置。"; Raise(); return; }
            Scheduler.SetAutomatic(value); Settings.AutoEnabled = value; Settings.Save();
            if (!value) automaticCapture?.Cancel(); else nextAutomatic = DateTimeOffset.MinValue;
            if (!value) { deferredAutomatic = null; deferredAutomaticStatus = null; }
            captureStop = capture.SetContinuousAsync(value);
            Status = value ? "自动识别已开启 · 后台运行，不打断游戏" : "自动识别已关闭 · 仍可按快捷键识别";
            Raise(); Raise(nameof(LiveRefreshHint)); AutomaticChanged?.Invoke();
        }
    }
    private RecognizedLine? selectedLine;
    public RecognizedLine? SelectedLine
    {
        get => selectedLine;
        set { if (!Set(ref selectedLine, value)) return; LineWords.Clear(); if (value is not null) foreach (var word in value.Words) LineWords.Add(word); }
    }
    private SavedWord? selectedWord;
    public SavedWord? SelectedWord
    {
        get => selectedWord;
        set
        {
            if (refreshingWords && value is null) return;
            var sameWord = value is not null && selectedWord?.Id == value.Id;
            if (!Set(ref selectedWord, value)) return;
            if (sameWord) return; // Metadata/count changes must not rewrite an editor or clear its explanation.
            History.Clear();
            if (value is not null)
            {
                DetailWord = value.Word; DetailPhonetic = value.Phonetic; DetailTranslation = value.Translation;
                Notes = value.Notes; Mastered = value.Mastered;
                foreach (var encounter in Store.History(value.Id)) History.Add(encounter);
                SelectedEncounter = History.FirstOrDefault();
            }
            else { DetailWord = "选择一个单词"; DetailTranslation = "查过的词，会带着游戏场景留在这里。"; DetailPhonetic = ""; SelectedEncounter = null; }
        }
    }
    private Encounter? selectedEncounter;
    public Encounter? SelectedEncounter
    {
        get => selectedEncounter;
        set
        {
            if (!Set(ref selectedEncounter, value)) return;
            DetailSentence = value?.Sentence ?? ""; AiExplanation = "";
            try { HistoryImage = value is not null && File.Exists(value.ImagePath) ? CapturedFrame.Image(File.ReadAllBytes(value.ImagePath)) : null; }
            catch (IOException) { HistoryImage = null; }
        }
    }
    private BitmapImage? historyImage;
    public BitmapImage? HistoryImage { get => historyImage; private set => Set(ref historyImage, value); }
    private string detailWord = "选择一个单词", detailPhonetic = "", detailTranslation = "查过的词，会带着游戏场景留在这里。", detailSentence = "", notes = "", aiExplanation = "";
    public string DetailWord { get => detailWord; private set => Set(ref detailWord, value); }
    public string DetailPhonetic { get => detailPhonetic; private set => Set(ref detailPhonetic, value); }
    public string DetailTranslation { get => detailTranslation; private set => Set(ref detailTranslation, value); }
    public string DetailSentence { get => detailSentence; private set => Set(ref detailSentence, value); }
    public string Notes { get => notes; set => Set(ref notes, value); }
    public string AiExplanation { get => aiExplanation; private set => Set(ref aiExplanation, value); }
    private bool mastered;
    public bool Mastered { get => mastered; set => Set(ref mastered, value); }
    private string search = "", gameFilter = "全部游戏";
    public string Search { get => search; set { if (Set(ref search, value)) RefreshWords(); } }
    public string GameFilter { get => gameFilter; set { if (refreshingWords) return; if (Set(ref gameFilter, value ?? "全部游戏")) RefreshWords(); } }
    public string WordCount => $"{Store.Words().Count} 个有故事的单词";
    public Recall? LastRecall { get; private set; }
    public MainViewModel(HttpClient? httpClient = null)
    {
        http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrWhiteSpace(Settings.AiBaseUrl) && !string.IsNullOrWhiteSpace(Settings.AiModel))
            aiConfigurationStatus = $"已加载保存的 AI 配置 · {Settings.AiModel} · 点击测试验证连接。";
        var dispatcher = Dispatcher.CurrentDispatcher;
        presentation.Changed += () =>
        {
            Raise(nameof(IsAutomaticRefreshPaused)); Raise(nameof(LiveRefreshHint));
            if (!presentation.IsHeld && !disposed) dispatcher.BeginInvoke(new Action(FlushAutomaticPresentation), DispatcherPriority.Background);
        };
        obs = new ObsCaptureService(() => ObsConnectionOptions.FromSettings(Settings));
        tracker = new(Store, Dictionary);
        Scheduler.Completed += (request, result) =>
        {
            if (!Scheduler.IsCurrent(request) || disposed) return;
            if (request.TestOnly) { Status = $"官方 API 测试成功 · {result.Lines.Count} 行文字 · {result.Elapsed.TotalSeconds:0.0} 秒"; return; }
            var recalls = tracker.Observe(result, request.Trigger);
            if (recalls.Count > 0)
            {
                LastRecall = recalls[0];
                if (request.Trigger == TriggerKind.Automatic && !presentation.IsHeld) RecallAvailable?.Invoke(recalls[0]);
            }
            if (request.Trigger == TriggerKind.Automatic && presentation.IsHeld)
            {
                // Keep the displayed frame and every bound collection untouched. Only the newest result is retained.
                deferredAutomatic = (request, result); return;
            }
            if (request.Trigger == TriggerKind.Manual) { deferredAutomatic = null; deferredAutomaticStatus = null; }
            DisplayRecognition(result, request.Trigger);
            if (request.Trigger == TriggerKind.Manual) ShowLookup?.Invoke();
        };
        Scheduler.Failed += (error, trigger) =>
        {
            var previousStatus = Status;
            var message = error.Message;
            if (error is not OcrRateLimitException && trigger == TriggerKind.Automatic && IsAutomatic) { IsAutomatic = false; message = "自动识别已暂停：" + error.Message; }
            if (trigger == TriggerKind.Automatic && presentation.IsHeld) { Status = previousStatus; deferredAutomaticStatus = message; }
            else Status = message;
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += async (_, _) => await AutomaticTick(); timer.Start();
        RefreshSources(); RefreshWords();
        // Restoring a window handle across processes is unsafe; each launch starts explicitly paused.
        Settings.AutoEnabled = false;
    }
    private bool lookupIsOpen;
    public bool LookupIsOpen { get => lookupIsOpen; set { lookupIsOpen = value; presentation.SetHeld(lookupInteraction, value); } }
    private void DisplayRecognition(RecognitionResult result, TriggerKind trigger)
    {
        if (trigger == TriggerKind.Automatic && Frame is not null && result.Frame.Timestamp < Frame.Timestamp) return;
        var unchanged = Result is not null && Result.Frame.Source == result.Frame.Source && Result.Frame.Region == result.Frame.Region
            && Result.Lines.SequenceEqual(result.Lines) && Result.Frame.FullPng.AsSpan().SequenceEqual(result.Frame.FullPng);
        if (trigger == TriggerKind.Manual || !unchanged) Present(result);
        Status = $"{result.Engine} · {result.Lines.Count} 行英文 · {result.Elapsed.TotalMilliseconds:0} ms · {result.Frame.Timestamp:HH:mm:ss}";
        RefreshWords();
    }
    internal void FlushAutomaticPresentation()
    {
        if (disposed || presentation.IsHeld) return;
        var pending = deferredAutomatic; deferredAutomatic = null;
        if (pending is { } latest && Scheduler.IsCurrent(latest.Request)) DisplayRecognition(latest.Result, TriggerKind.Automatic);
        if (deferredAutomaticStatus is { } message) { deferredAutomaticStatus = null; Status = message; }
    }
    internal (int Started, int Stopped, bool Active) CaptureMetrics => (capture.WgcSessionsStarted, capture.WgcSessionsStopped, capture.HasActiveSession);
    internal string CaptureDebugState => capture.DebugState;
    private IOcrProvider Provider() => Settings.Engine == OcrEngineKind.Local ? local : new PaddleCloudOcrProvider(http, AppSettings.Unprotect(Settings.OcrSecret), TimeSpan.FromSeconds(Settings.CloudTimeoutSeconds));
    private async Task<CapturedFrame> CaptureCurrentAsync(WindowSource source, string game, CropRegion? region, CancellationToken token)
    {
        captureOperations++;
        try { return source.IsObs ? await obs.CaptureAsync(source, game, region, token) : await capture.CaptureAsync(source, game, region, token); }
        finally { captureOperations--; }
    }
    public void RefreshSources()
    {
        var key = SelectedSource?.Key;
        refreshingSources = true;
        try { Sources.Clear(); foreach (var source in obsSources.Concat(CaptureService.ListWindows())) Sources.Add(source); }
        finally { refreshingSources = false; }
        SelectedSource = Sources.FirstOrDefault(s => s.Key == key) ?? Sources.FirstOrDefault();
        Raise(nameof(SelectedSource));
    }
    public async Task ConnectObsAsync(bool selectPreferred = true)
    {
        if (connectingObs || disposed) return;
        connectingObs = true; ObsStatus = "正在连接本机 OBS…";
        try
        {
            // Source enumeration is not canceled by the selection changes it produces.
            obsSources = await obs.ListSourcesAsync(CancellationToken.None);
            if (disposed) return;
            var preferred = obsSources.FirstOrDefault(s => s.Key == Settings.PreferredObsSourceKey)
                ?? obsSources.FirstOrDefault(s => s.Kind == CaptureSourceKind.ObsInput && s.IsActive)
                ?? obsSources.First();
            RefreshSources();
            if (selectPreferred) SelectedSource = preferred;
            ObsStatus = "已连接 OBS · 直接读取纯画面，支持 OBS 被遮挡或最小化";
            Status = $"已加载 {obsSources.Count} 个 OBS 来源 · 请选择捕获源或当前输出画面";
        }
        catch (Exception e) { if (!disposed) { ObsStatus = e.Message; Status = e.Message; } }
        finally { connectingObs = false; }
    }
    public void Invalidate()
    {
        deferredAutomatic = null; deferredAutomaticStatus = null;
        Scheduler.Invalidate(); sourceCancellation.Cancel(); sourceCancellation.Dispose(); sourceCancellation = new();
        automaticCapture?.Cancel(); nextAutomatic = DateTimeOffset.MinValue;
        captureStop = capture.ResetAsync();
    }
    public void ApplySettings()
    {
        Invalidate(); obsReset = obs.ResetAsync(); Settings.Save(); ResetAiConfiguration(); Raise(nameof(EngineLabel)); Status = "设置已保存 · 识别引擎已更新";
    }
    public void SaveAiConfiguration(string address, string model, string secret)
    {
        AiExplanationService.ValidateConfiguration(address, model);
        var candidate = Settings.Copy();
        candidate.AiBaseUrl = address.Trim().TrimEnd('/'); candidate.AiModel = model.Trim(); candidate.AiSecret = AppSettings.Protect(secret.Trim());
        candidate.Save();
        Settings.AiBaseUrl = candidate.AiBaseUrl; Settings.AiModel = candidate.AiModel; Settings.AiSecret = candidate.AiSecret;
        ResetAiConfiguration();
        AiConfigurationStatus = $"AI 配置已保存 · {Settings.AiModel} · 可点击测试确认连接";
        Status = AiConfigurationStatus;
    }
    public void ReportAiConfigurationError(string message) { AiConfigurationStatus = "AI 配置未保存：" + message; Status = AiConfigurationStatus; }
    private void ResetAiConfiguration()
    {
        aiConfigurationVersion++; aiConfigurationCancellation.Cancel(); aiConfigurationCancellation.Dispose(); aiConfigurationCancellation = new();
        AiExplanation = "";
        AiConfigurationStatus = string.IsNullOrWhiteSpace(Settings.AiBaseUrl) || string.IsNullOrWhiteSpace(Settings.AiModel)
            ? "尚未配置 AI 服务，离线词典仍可使用。" : $"AI 配置已更新 · {Settings.AiModel} · 请点击测试验证连接。";
    }
    public async Task TestAiConnectionAsync()
    {
        if (IsAiTesting) return;
        IsAiTesting = true;
        var version = aiConfigurationVersion; var snapshot = Settings.Copy();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(aiConfigurationCancellation.Token);
        AiConfigurationStatus = "正在测试 AI 服务…";
        try
        {
            await new AiExplanationService(http).TestConnectionAsync(snapshot, cancellation.Token);
            if (disposed || version != aiConfigurationVersion) return;
            AiConfigurationStatus = $"连接成功 · {snapshot.AiModel} · {watch.Elapsed.TotalSeconds:0.0} 秒\n接口：{AiExplanationService.ResolveEndpoint(snapshot.AiBaseUrl).GetLeftPart(UriPartial.Path)}";
            Status = "AI 服务测试成功，可在单词详情点击「解释当前语境」。";
        }
        catch (OperationCanceledException) { if (!disposed && version == aiConfigurationVersion) AiConfigurationStatus = "测试已取消。"; }
        catch (Exception e) { if (!disposed && version == aiConfigurationVersion) { AiConfigurationStatus = "测试未通过：" + e.Message; Status = AiConfigurationStatus; } }
        finally { IsAiTesting = false; }
    }
    public void SetCrop(CropRegion? crop) { Crop = crop; Invalidate(); Raise(nameof(RegionLabel)); FrameChanged?.Invoke(); }
    public async Task PreviewAsync()
    {
        deferredAutomatic = null; deferredAutomaticStatus = null;
        if (SelectedSource is null) { Status = "请选择画面来源。"; return; }
        var generation = Scheduler.Generation;
        try
        {
            var frame = await CaptureCurrentAsync(SelectedSource, GameName, Crop, sourceCancellation.Token);
            if (generation != Scheduler.Generation || disposed) return;
            Frame = frame; Result = null; Lines.Clear(); SelectedLine = null; Preview = CapturedFrame.Image(frame.FullPng); FrameChanged?.Invoke();
            Status = $"画面预览 · {frame.Width} × {frame.Height} · {(SelectedSource?.IsObs == true ? "OBS 直接画面" : capture.LastBackend)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = e.Message; }
    }
    public async Task RecognizeNowAsync()
    {
        if (SelectedSource is null) { Status = "请选择画面来源。"; return; }
        var sequence = ++manualSequence; var generation = Scheduler.Generation; var provider = Provider();
        Status = "正在抓取当前游戏画面…";
        try
        {
            var frame = await CaptureCurrentAsync(SelectedSource, GameName, Crop, sourceCancellation.Token);
            if (sequence != manualSequence || generation != Scheduler.Generation || disposed) return;
            Status = Scheduler.IsBusy ? "当前截图已排队 · 完成后优先识别" : "正在识别当前截图…";
            await Scheduler.EnqueueAsync(new(frame, TriggerKind.Manual, generation, provider));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = e.Message; }
    }
    private async Task AutomaticTick()
    {
        if (!IsAutomatic || SelectedSource is null || Scheduler.IsBusy || automaticCapture is not null || DateTimeOffset.UtcNow < nextAutomatic
            || (Settings.Engine == OcrEngineKind.PaddleCloud && DateTimeOffset.UtcNow < Scheduler.RetryAt)) return;
        var generation = Scheduler.Generation; var provider = Provider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sourceCancellation.Token);
        automaticCapture = cancellation;
        nextAutomatic = DateTimeOffset.UtcNow.AddSeconds(Settings.Engine == OcrEngineKind.Local ? Settings.LocalIntervalSeconds : Settings.CloudIntervalSeconds);
        try
        {
            var frame = await CaptureCurrentAsync(SelectedSource, GameName, Crop, cancellation.Token);
            if (!IsAutomatic || cancellation.IsCancellationRequested || generation != Scheduler.Generation || disposed) return;
            await Scheduler.EnqueueAsync(new(frame, TriggerKind.Automatic, generation, provider));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { IsAutomatic = false; Status = "自动识别已暂停：" + e.Message; }
        finally { automaticCapture = null; }
    }
    public void Present(RecognitionResult result)
    {
        Result = result; Frame = result.Frame; Preview = CapturedFrame.Image(result.Frame.FullPng);
        Lines.Clear(); foreach (var line in result.Lines) Lines.Add(line); SelectedLine = Lines.FirstOrDefault(); FrameChanged?.Invoke();
    }
    public void Learn(string observed)
    {
        if (Frame is null || SelectedLine is null || string.IsNullOrWhiteSpace(observed)) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(observed, "^[A-Za-z]+(?:['’\\-][A-Za-z]+)*$")) { Status = "请输入一个英文单词，可包含连字符或撇号。"; return; }
        var encounter = tracker.Learn(observed, SceneContext.ForLine(Result, SelectedLine), Frame);
        Search = ""; GameFilter = "全部游戏"; RefreshWords();
        SelectedWord = Words.FirstOrDefault(w => w.Word == encounter.Word); SelectedEncounter = encounter;
        Status = $"已保存 {observed} · 词义、原句和场景已关联";
    }
    public void OpenRecall()
    {
        if (LastRecall is null) { Status = "还没有再次遇见的单词，可先查一个词。"; return; }
        Search = ""; GameFilter = "全部游戏"; RefreshWords();
        SelectedWord = Words.FirstOrDefault(w => w.Id == LastRecall.Word.Id); SelectedEncounter = LastRecall.Previous;
        Status = $"上次遇见 {LastRecall.Word.Word} · {LastRecall.Previous.Game} · {LastRecall.Previous.At:g}";
    }
    public void RefreshWords()
    {
        if (refreshingWords) return;
        refreshingWords = true;
        try
        {
        var selectedId = SelectedWord?.Id;
        var encounterId = SelectedEncounter?.Id;
        var draftNotes = Notes; var draftMastered = Mastered;
        var draftExplanation = AiExplanation;
        var refreshed = Store.Words(Search, GameFilter == "全部游戏" ? "" : GameFilter);
        var games = Store.Words().SelectMany(w => Store.History(w.Id)).Select(e => e.Game).Distinct().OrderBy(g => g).ToArray();
        if (!Games.Skip(1).SequenceEqual(games)) { Games.Clear(); Games.Add("全部游戏"); foreach (var game in games) Games.Add(game); }
        if (Words.SequenceEqual(refreshed)) { Raise(nameof(WordCount)); Raise(nameof(GameFilter)); return; }
        Words.Clear(); foreach (var word in refreshed) Words.Add(word);
        if (selectedId is not null)
        {
            SelectedWord = Words.FirstOrDefault(w => w.Id == selectedId);
            if (SelectedWord is not null)
            {
                RefreshSelectedHistory(SelectedWord.Id);
                SelectedEncounter = History.FirstOrDefault(e => e.Id == encounterId) ?? History.FirstOrDefault();
                Notes = draftNotes; Mastered = draftMastered;
                if (SelectedEncounter?.Id == encounterId) AiExplanation = draftExplanation;
            }
        }
        Raise(nameof(WordCount));
        Raise(nameof(SelectedWord)); Raise(nameof(GameFilter));
        }
        finally { refreshingWords = false; }
    }
    private void RefreshSelectedHistory(long wordId)
    {
        var entries = Store.History(wordId);
        for (var i = 0; i < entries.Count; i++)
        {
            var existing = History.Select((entry, index) => (entry, index)).FirstOrDefault(pair => pair.entry.Id == entries[i].Id);
            if (existing.entry is null) History.Insert(i, entries[i]);
            else if (existing.index != i) History.Move(existing.index, i);
        }
        while (History.Count > entries.Count) History.RemoveAt(History.Count - 1);
    }
    public void SaveWord()
    {
        if (SelectedWord is null) return;
        Store.UpdateWord(SelectedWord.Id, Mastered, Notes); RefreshWords(); Status = "学习状态和备注已保存。";
    }
    public void DeleteSelected()
    {
        if (SelectedWord is null) return;
        tracker.Forget(SelectedWord.Id); Store.DeleteWord(SelectedWord.Id); SelectedWord = null; RefreshWords(); LastRecall = null;
    }
    public void ClearRecords() { Store.Clear(); tracker.Reset(); SelectedWord = null; LastRecall = null; RefreshWords(); }
    public async Task ExplainAsync()
    {
        if (SelectedWord is null || SelectedEncounter is null) { Status = "请先选择一个已保存的词和原句。"; return; }
        var encounterId = SelectedEncounter.Id;
        var snapshot = Settings.Copy(); var version = aiConfigurationVersion;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sourceCancellation.Token, aiConfigurationCancellation.Token);
        try
        {
            AiExplanationService.ValidateConfiguration(snapshot.AiBaseUrl, snapshot.AiModel);
            var key = AiExplanationService.CacheKey(snapshot, DetailWord, DetailSentence);
            if (Store.GetExplanation(key) is { } cached) { AiExplanation = cached; return; }
            AiExplanation = "正在结合原句解释…";
            var explanation = await new AiExplanationService(http).ExplainAsync(snapshot, DetailWord, DetailSentence, cancellation.Token);
            if (disposed || version != aiConfigurationVersion) return;
            Store.SaveExplanation(key, explanation); if (SelectedEncounter?.Id == encounterId) AiExplanation = explanation;
        }
        catch (OperationCanceledException) { if (!disposed && version == aiConfigurationVersion && SelectedEncounter?.Id == encounterId) AiExplanation = "解释已取消。"; }
        catch (Exception e) { if (!disposed && version == aiConfigurationVersion && SelectedEncounter?.Id == encounterId) AiExplanation = "解释未完成：" + e.Message; }
    }
    public async Task TestCloudAsync()
    {
        if (SelectedSource is null) { Status = "请先选择画面来源。"; return; }
        Status = "正在用当前识别区域测试官方 PP-OCRv6…";
        var generation = Scheduler.Generation;
        try
        {
            var provider = new PaddleCloudOcrProvider(http, AppSettings.Unprotect(Settings.OcrSecret), TimeSpan.FromSeconds(Settings.CloudTimeoutSeconds));
            var frame = await CaptureCurrentAsync(SelectedSource, GameName, Crop, sourceCancellation.Token);
            await Scheduler.EnqueueAsync(new(frame, TriggerKind.Manual, generation, provider, TestOnly: true));
        }
        catch (OperationCanceledException) { Status = "连接测试已取消。"; }
        catch (Exception e) { Status = e.Message; }
    }
    public async Task ShutdownAsync()
    {
        disposed = true; timer.Stop(); Scheduler.Dispose(); sourceCancellation.Cancel(); automaticCapture?.Cancel();
        aiConfigurationCancellation.Cancel();
        captureStop = capture.SetContinuousAsync(false);
        while (Scheduler.IsBusy || automaticCapture is not null || captureOperations > 0 || connectingObs) await Task.Delay(50);
        await captureStop;
        await obsReset;
        Dispose();
    }
    public void Dispose() { capture.Dispose(); obs.Dispose(); local.Dispose(); http.Dispose(); Store.Dispose(); Dictionary.Dispose(); sourceCancellation.Dispose(); aiConfigurationCancellation.Dispose(); }
}
