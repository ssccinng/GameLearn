using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameLearn;

public sealed class AppSettings
{
    public OcrEngineKind Engine { get; set; }
    public bool PreferTextRegions { get; set; } = true;
    public double LocalIntervalSeconds { get; set; } = 1;
    public double CloudIntervalSeconds { get; set; } = 5;
    public int CloudTimeoutSeconds { get; set; } = 30;
    public string OcrSecret { get; set; } = "";
    public string AiSecret { get; set; } = "";
    public string AiBaseUrl { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+E";
    public string AutoHotkey { get; set; } = "Ctrl+Alt+A";
    public string RecallHotkey { get; set; } = "Ctrl+Alt+R";
    public bool AutoEnabled { get; set; }
    public bool ObsUseLocalConfiguration { get; set; } = true;
    public int ObsPort { get; set; } = 4455;
    public string ObsSecret { get; set; } = "";
    public string PreferredObsSourceKey { get; set; } = "";
    public bool PreferFloatingMode { get; set; }
    public bool FloatingBarCollapsed { get; set; }
    public AppSettings Copy() => (AppSettings)MemberwiseClone();
    // Physical screen coordinates, independent of a monitor's WPF scale.
    public double? FloatingBarX { get; set; }
    public double? FloatingBarY { get; set; }
    public static string DataDirectory => Environment.GetEnvironmentVariable("GAMELEARN_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLearn");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");
    public static AppSettings Load()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(FilePath)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (JsonException) { File.Copy(FilePath, FilePath + ".invalid-" + DateTime.UtcNow.Ticks); return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }
    }
}
