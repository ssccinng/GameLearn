using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLearn;

public enum OcrEngineKind { Local, PaddleCloud }
public enum TriggerKind { Manual, Automatic }
public enum CaptureSourceKind { Window, ObsProgram, ObsScene, ObsInput }
public record WindowSource(nint Handle, string Title, string ProcessName, CaptureSourceKind Kind = CaptureSourceKind.Window, string? ObsName = null, bool IsActive = false)
{
    public bool IsObs => Kind != CaptureSourceKind.Window;
    public bool IsObsProjector => Kind == CaptureSourceKind.Window && ProcessName.Equals("obs64", StringComparison.OrdinalIgnoreCase)
        && (Title.Contains("Projector", StringComparison.OrdinalIgnoreCase) || Title.Contains("投影", StringComparison.OrdinalIgnoreCase)
            || Title.Contains("投射", StringComparison.OrdinalIgnoreCase));
    public string SelectionHint => IsObsProjector ? "OBS 投影 · 窗口 / 全屏均可画面选词"
        : IsObs ? "OBS 纯画面 · 悬浮栏查词，无桌面位置"
        : ProcessName.Equals("obs64", StringComparison.OrdinalIgnoreCase) ? "OBS 界面窗口 · 建议选择投影或纯画面来源"
        : "窗口画面 · 支持画面选词";
    public string Key => IsObs ? $"{Kind}:{ObsName}" : $"window:{Handle}";
    public override string ToString() => Kind switch
    {
        CaptureSourceKind.ObsProgram => "OBS · 当前输出画面（纯画面）",
        CaptureSourceKind.ObsScene => $"OBS 场景 · {Title}",
        CaptureSourceKind.ObsInput => $"OBS 捕获源 · {Title}" + (IsActive ? "  [当前场景]" : ""),
        _ => $"{Title}  ·  {ProcessName}" + (ProcessName == "obs64" && !Title.Contains("Projector", StringComparison.OrdinalIgnoreCase) ? "（界面窗口）" : "")
    };
}
public record PixelRect(double X, double Y, double Width, double Height);
public record CropRegion(double X, double Y, double Width, double Height);
public record CapturedFrame(Guid Id, DateTimeOffset Timestamp, string Game, string Source,
    byte[] FullPng, byte[] OcrPng, int Width, int Height, PixelRect Region, string Fingerprint)
{
    public PixelRect? DesktopBounds { get; init; }
    public bool CapturedClientOnly { get; init; }
    public string? SourceKey { get; init; }
    public static BitmapImage Image(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}
public record RecognizedLine(string Text, double Confidence, PixelRect Bounds)
{
    public IReadOnlyList<RecognizedLine>? Fragments { get; init; }
    public IEnumerable<string> Words => Regex.Matches(Text, @"[A-Za-z]+(?:['’\-][A-Za-z]+)*").Select(m => m.Value);
}
public sealed record PrioritizedLine(RecognizedLine Line, IReadOnlyList<string> Words, double Score, int PriorityLevel, string PriorityLabel, Brush CardBrush)
{
    public string Text => Line.Text;
    public IReadOnlyList<string> DisplayWords => Regex.Matches(Text, @"\S+").Select(m => m.Value).ToArray();
}
public static class LearningPriority
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","about","after","again","all","also","an","and","another","any","are","around","as","ask","at","back","be","because","been","before","being","but","by","can","come","could","day","did","do","down","each","even","every","find","first","for","from","get","go","good","got","had","has","have","he","her","here","him","his","how","i","if","in","into","is","it","its","just","know","like","little","look","make","man","me","more","most","much","my","need","new","no","not","now","of","off","on","one","only","or","other","our","out","over","people","read","right","said","same","see","she","should","so","some","such","than","that","the","their","them","then","there","these","they","think","this","time","to","too","two","under","up","us","use","very","want","was","way","we","well","were","what","when","where","which","who","will","with","would","yeah","you","your"
    };
    public static IReadOnlyList<PrioritizedLine> Rank(IEnumerable<RecognizedLine> lines, OfflineDictionary dictionary, IReadOnlyDictionary<string, SavedWord> saved)
        => lines.Select(line => Score(line, dictionary, saved)).OrderByDescending(line => line.Score).ThenBy(line => line.Line.Bounds.Y).ToArray();
    public static PrioritizedLine Score(RecognizedLine line, OfflineDictionary dictionary, IReadOnlyDictionary<string, SavedWord> saved)
    {
        var words = line.Words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (words.Length == 0) return new(line, words, 0, 0, "无英文词", Brushes.Transparent);
        var score = 0d;
        foreach (var observed in words)
        {
            var entry = dictionary.Lookup(observed);
            var normalized = entry.Word.ToLowerInvariant();
            if (CommonWords.Contains(normalized)) score += 0.05;
            else if (saved.TryGetValue(normalized, out var known)) score += known.Mastered ? 0.15 : 1.0;
            else score += 2.25;
            if (entry.Translation.Contains("未收录", StringComparison.Ordinal)) score -= 2.0;
            if (normalized.Length >= 8) score += 0.85;
            else if (normalized.Length >= 6) score += 0.35;
            if (observed.Contains('-') || observed.Contains('\'') || observed.Contains('’')) score += 0.35;
        }
        score += Math.Min(1.5, Math.Max(0, words.Length - 4) * 0.18);
        score *= Math.Clamp(line.Confidence, 0, 1);
        var level = score >= 8 ? 3 : score >= 4 ? 2 : score >= 1.5 ? 1 : 0;
        var label = level switch { 3 => "优先学习", 2 => "值得看看", 1 => "可以复习", _ => "轻松复习" };
        var color = level switch
        {
            3 => new SolidColorBrush(Color.FromRgb(47, 79, 67)),
            2 => new SolidColorBrush(Color.FromRgb(38, 66, 69)),
            1 => new SolidColorBrush(Color.FromRgb(33, 55, 62)),
            _ => new SolidColorBrush(Color.FromRgb(28, 48, 56))
        };
        color.Freeze();
        return new(line, words, Math.Round(score, 1), level, label, color);
    }
}
public record RecognitionResult(CapturedFrame Frame, IReadOnlyList<RecognizedLine> Lines, string Engine, TimeSpan Elapsed);
public static class SceneContext
{
    public static RecognizedLine ForLine(RecognitionResult? result, RecognizedLine line)
    {
        if (result is null) return line;
        var candidates = result.Lines.Where(other => Math.Abs(other.Bounds.X - line.Bounds.X) <= Math.Max(60, line.Bounds.Height * 1.2))
            .OrderBy(other => other.Bounds.Y).ToList();
        var index = candidates.IndexOf(line);
        if (index < 0) return line;
        var first = index; var last = index;
        while (first > 0 && index - first < 2 && Neighbors(candidates[first - 1], candidates[first])) first--;
        while (last + 1 < candidates.Count && last - index < 2 && Neighbors(candidates[last], candidates[last + 1])) last++;
        return line with { Text = string.Join("\n", candidates.Skip(first).Take(last - first + 1).Select(l => l.Text)) };
    }
    private static bool Neighbors(RecognizedLine a, RecognizedLine b) => b.Bounds.Y - a.Bounds.Y - a.Bounds.Height <= Math.Max(a.Bounds.Height, b.Bounds.Height) * 0.7;
}
public record DictionaryEntry(string Word, string Phonetic, string Translation, string Observed, string? ObservedPhonetic = null, string? ObservedTranslation = null);
public record SavedWord(long Id, string Word, string Phonetic, string Translation, bool Mastered, string Notes, int Encounters, string LastGame);
public record Encounter(long Id, string Word, string Observed, string Sentence, string Game, DateTimeOffset At, string ImagePath, PixelRect Bounds);
public record Recall(SavedWord Word, Encounter Previous, Encounter Current);
public record RecentRecognition(long Id, string Game, string Source, DateTimeOffset At, string Text);
public record RecognitionRequest(CapturedFrame Frame, TriggerKind Trigger, long Generation, IOcrProvider Provider, bool TestOnly = false);

public interface IOcrProvider
{
    string Name { get; }
    Task<RecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken);
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
