using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace GameLearn;

public enum OcrEngineKind { Local, PaddleCloud }
public enum TriggerKind { Manual, Automatic }
public enum CaptureSourceKind { Window, ObsProgram, ObsScene, ObsInput }
public record WindowSource(nint Handle, string Title, string ProcessName, CaptureSourceKind Kind = CaptureSourceKind.Window, string? ObsName = null, bool IsActive = false)
{
    public bool IsObs => Kind != CaptureSourceKind.Window;
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
    public IEnumerable<string> Words => Regex.Matches(Text, @"[A-Za-z]+(?:['’\-][A-Za-z]+)*").Select(m => m.Value);
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
public record DictionaryEntry(string Word, string Phonetic, string Translation, string Observed);
public record SavedWord(long Id, string Word, string Phonetic, string Translation, bool Mastered, string Notes, int Encounters, string LastGame);
public record Encounter(long Id, string Word, string Observed, string Sentence, string Game, DateTimeOffset At, string ImagePath, PixelRect Bounds);
public record Recall(SavedWord Word, Encounter Previous, Encounter Current);
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
