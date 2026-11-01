using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Aura;

public sealed class Settings
{
    public bool Paused { get; set; }
    /// Screenshot/OCR opt-in. OFF by default. v0.2: OCR still a stub; UIA path never needs it.
    public bool ScreenCaptureOptIn { get; set; } = false;
    /// Process names (without .exe) Aura never reads context from.
    public List<string> ExcludedApps { get; set; } = new() { "KeePass", "1Password", "Bitwarden" };
    /// v0.2 stub: targets the user allowed to auto-click (process|name). Not acted on yet; every click needs «Нажать».
    public List<string> AutoAllowed { get; set; } = new();
    public int DefaultTimerMinutes { get; set; } = 25;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aura");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        var s = new Settings(); s.Save(); return s;
    }
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt));
    }
    public bool IsExcluded(string process) =>
        ExcludedApps.Exists(a => string.Equals(a, process, StringComparison.OrdinalIgnoreCase));
}
