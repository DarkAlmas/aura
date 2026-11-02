using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Aura;

public sealed class Settings
{
    public bool Paused { get; set; }
    /// OCR fallback opt-in («OCR если UIA не нашёл»). OFF by default. Only used once per user-started search when UIA found nothing.
    public bool ScreenCaptureOptIn { get; set; } = false;
    /// Process names (without .exe) Aura never reads context from.
    public List<string> ExcludedApps { get; set; } = new() { "KeePass", "1Password", "Bitwarden" };
    /// v0.2.3 «разрешить авто». OFF by default. When ON: after a user-started «Найти» with exactly ONE match, the same 1 s «Делаю»+Стоп countdown starts automatically (never an instant/background click).
    public bool AutoClickOptIn { get; set; } = false;
    /// v0.4.0 «Действовать». OFF by default. When ON (and not paused): UIA scan of the foreground window every 2 s for learned buttons → «Делаю» 1 s + Стоп → click.
    public bool ActOptIn { get; set; } = false;
    /// Legacy v0.2 stub list (unused; kept so old settings.json still loads).
    public List<string> AutoAllowed { get; set; } = new();
    public int DefaultTimerMinutes { get; set; } = 25;
    // ---- v0.5.0 ----
    /// Action used by «Под курсором» / «Запомнить» by default.
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public ClickAction DefaultAction { get; set; } = ClickAction.Click;
    /// «запомнить картинку» — save a ≤120×60 PNG of the control to %AppData%\Aura\templates when remembering. OFF by default.
    public bool SaveTemplates { get; set; } = false;
    /// «При старте» opt-in + list.
    public bool StartupOptIn { get; set; } = false;
    public List<StartupAction> StartupActions { get; set; } = new();
    /// Delay between steps of a series / startup list.
    public int StepDelayMs { get; set; } = 800;
    public bool ClickSound { get; set; } = false;
    /// v0.5.2 profiles: apps where «действовать» stays silent even when ON.
    /// v0.5.7: background rule hints (toast only, never clicks). OFF by default.
    public bool RulesOptIn { get; set; } = false;
    public bool RulesQuiet { get; set; } = false; // v0.6.3: hint = short sound + tray tooltip, no balloon
    public List<string> ActDisabledApps { get; set; } = new();
    public bool ActDisabled(string p) => ActDisabledApps.Exists(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase));
    /// «ИИ (сеть)» — the ONLY switch that allows network. OFF by default.
    public bool AiOptIn { get; set; } = false;
    public string AiBaseUrl { get; set; } = Ai.ProxyBase;
    public string AiModel { get; set; } = "openai/gpt-oss-20b";
    /// v0.5.5: the key is never stored in plain text. On disk only «AiKeyProtected» (DPAPI, CurrentUser, base64).
    /// Old plain «AiKey» from settings.json is read once, protected and removed on the next Save().
    [System.Text.Json.Serialization.JsonIgnore] public string AiKey { get => Unprotect(AiKeyProtected); set => AiKeyProtected = Protect(value); }
    public string AiKeyProtected { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("AiKey")]
    public string? LegacyAiKey { get => null; set { if (!string.IsNullOrEmpty(value)) AiKey = value; } }
    [System.Text.Json.Serialization.JsonIgnore] public bool HadLegacyKey;
    static string Protect(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        try { return Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(v), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); } catch { return ""; }
    }
    static string Unprotect(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        try { return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(v), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); } catch { return ""; }
    }
    /// Attach OCR text of the window to the AI request (needs OCR opt-in too). OFF by default.
    public bool AiAttachText { get; set; } = false;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aura");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var txt = File.ReadAllText(FilePath); var r = JsonSerializer.Deserialize<Settings>(txt) ?? new();
                if (txt.Contains("\"AiKey\"")) r.Save(); // rewrite without the plain key
                return r;
            }
        }
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
