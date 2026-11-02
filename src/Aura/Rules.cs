using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Aura;

/// v0.5.7 «Если вижу X → предложить Y». Rules ONLY suggest (toast / overlay list). Never click by themselves:
/// running a suggestion is a user click on «▶» which goes through «Делаю» 1 с + Стоп. Stored in %AppData%\Aura\rules.json.
public sealed class Rule
{
    public string Process { get; set; } = "";      // "" = any app
    public string When { get; set; } = "";         // text of a visible control (UIA Name, contains, case-insensitive)
    public string Suggest { get; set; } = "";      // name of a button to press (learned or visible)
    public bool Enabled { get; set; } = true;
    public int Shown { get; set; }   // v0.6.7 stats: how often suggested / how often the user pressed ▶
    public int Used { get; set; }
    string Stat => Shown + Used == 0 ? "" : $" · {Used}/{Shown}{(Shown >= 5 && Used == 0 ? " (не нужна?)" : "")}";
    public override string ToString() => $"{(Enabled ? "" : "⏸ ")}Вижу «{When}» → предложить «{Suggest}»{(Process.Length > 0 ? " · " + Process : " · везде")}{Stat}";
}

public static class Rules
{
    static string FilePath => Path.Combine(Settings.Dir, "rules.json");
    static List<Rule>? _cache;
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    public static List<Rule> List()
    {
        if (_cache != null) return _cache;
        _cache = Settings.LoadJson<List<Rule>>(FilePath) ?? new();
        return _cache;
    }
    public static void Save() => Settings.AtomicWrite(FilePath, JsonSerializer.Serialize(List(), Opt));
    // v0.6.5: trim; «вижу» must be ≥2 chars (empty would match every window); no duplicates
    public static void Add(Rule r)
    {
        r.When = (r.When ?? "").Trim(); r.Suggest = (r.Suggest ?? "").Trim();
        if (r.When.Length < 2 || r.Suggest.Length == 0) return;
        // v0.9.15: bounded — long text trimmed, max 200 rules, never for an excluded app
        if (r.When.Length > 100) r.When = r.When[..100]; if (r.Suggest.Length > 60) r.Suggest = r.Suggest[..60];
        if (List().Count >= 200 || (!string.IsNullOrEmpty(r.Process) && App.Current.Settings.IsExcluded(r.Process))) return;
        if (List().Any(x => x.When == r.When && x.Suggest == r.Suggest && x.Process == r.Process)) return;
        List().Add(r); Save();
    }
    public static void Remove(Rule r) { List().Remove(r); Save(); }

    public static int Export(string path) { File.WriteAllText(path, JsonSerializer.Serialize(List(), Opt)); return List().Count; }
    // v0.7.3 import validation: ≤1 MB file, ≤200 rules, text ≤100 chars, no control chars, process = bare name (no path), stats reset,
    // imported rules arrive DISABLED (⏸) so nothing new starts suggesting until you switch it on.
    public static int Import(string path)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException("файл больше 1 МБ");
        var src = JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(path)) ?? new(); int n = 0;
        static string Clean(string? t) => new string((t ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        foreach (var raw in src.Take(200))
        {
            var r = new Rule { When = Clean(raw.When), Suggest = Clean(raw.Suggest), Process = Clean(raw.Process), Enabled = false };
            if (r.When.Length < 2 || r.When.Length > 100 || r.Suggest.Length == 0 || r.Suggest.Length > 100) continue;
            if (r.Process.Length > 64 || r.Process.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) continue;
            if (List().Count >= 500) break;
            if (!List().Any(x => x.When == r.When && x.Suggest == r.Suggest && x.Process == r.Process)) { List().Add(r); n++; }
        }
        Save(); Sync(); return n;
    }
    public static IEnumerable<Rule> For(string process) => List().Where(r => r.Enabled && r.When.Length > 0 && r.Suggest.Length > 0 &&
        (r.Process.Length == 0 || string.Equals(r.Process, process, StringComparison.OrdinalIgnoreCase)));

    /// Which rules fire for the given UIA snapshot (names only, no screen capture).
    public static List<Rule> Hits(string process, List<UiaFinder.Target> uia) =>
        For(process).Where(r => r.When.Trim().Length >= 2 && uia.Any(t => t.Name.Contains(r.When, StringComparison.OrdinalIgnoreCase))).ToList();

    // ---- background «подсказки» (opt-in RulesOptIn, OFF by default): every 3 s one UIA read of the FOREGROUND window, toast only.
    static DispatcherTimer? _t; static bool _busy;
    static readonly Dictionary<string, DateTime> _cool = new();
    public static void Sync()
    {
        var s = App.Current.Settings; bool on = s.RulesOptIn && !s.Paused && List().Any(r => r.Enabled);
        if (on && _t == null) { _t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; _t.Tick += Tick; _t.Start(); }
        if (!on && _t != null) { _t.Stop(); _t = null; }
    }
    static async void Tick(object? s, EventArgs e)
    {
        var st = App.Current.Settings;
        if (_busy || st.Paused || !st.RulesOptIn || App.Current.ActionCts != null || DateTime.Now < App.Current.RulesSnoozeUntil) return; // v0.7.6: quiet while «Делаю»
        var fg = Native.Foreground();
        if (fg == null || fg.Pid == Environment.ProcessId || st.IsExcluded(fg.Process) || !For(fg.Process).Any()) return;
        _busy = true;
        try
        {
            var uia = await Task.Run(() => UiaFinder.Enumerate(fg.Hwnd));
            if (st.Paused || !st.RulesOptIn || App.Current.ActionCts != null || DateTime.Now < App.Current.RulesSnoozeUntil || Native.Foreground()?.Hwnd != fg.Hwnd) return; // v0.9.1: state may change during the read
            foreach (var r in Hits(fg.Process, uia))
            {
                var key = fg.Hwnd + "|" + r.When + "|" + r.Suggest;
                if (_cool.TryGetValue(key, out var u) && u > DateTime.Now) continue;
                if (_cool.Count > 200) foreach (var k in _cool.Where(c => c.Value < DateTime.Now).Select(c => c.Key).ToList()) _cool.Remove(k);
                _cool[key] = DateTime.Now.AddSeconds(60);
                App.Current.LastSuggestion = (r, fg); App.Current.NoteSuggestion();
                r.Shown++; try { Save(); } catch { }
                if (App.Current.Settings.RulesQuiet) { System.Media.SystemSounds.Asterisk.Play(); App.Current.TrayHint($"Подсказка: «{r.Suggest}»"); }
                else App.Current.ToastHint("Aura · подсказка", $"Вижу «{r.When}» в {fg.Process}. Нажать «{r.Suggest}»? Кликни сюда — «Делаю» 1 с + Стоп. Или {App.Current.HotkeyName} → ▶. Сам не жму.");
                break;
            }
        }
        catch { }
        finally { _busy = false; }
    }
}
