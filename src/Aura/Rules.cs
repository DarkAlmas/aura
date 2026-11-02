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
        try { _cache = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(FilePath)) ?? new() : new(); } catch { _cache = new(); }
        return _cache;
    }
    public static void Save() { Directory.CreateDirectory(Settings.Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(List(), Opt)); }
    // v0.6.5: trim; «вижу» must be ≥2 chars (empty would match every window); no duplicates
    public static void Add(Rule r)
    {
        r.When = (r.When ?? "").Trim(); r.Suggest = (r.Suggest ?? "").Trim();
        if (r.When.Length < 2 || r.Suggest.Length == 0) return;
        if (List().Any(x => x.When == r.When && x.Suggest == r.Suggest && x.Process == r.Process)) return;
        List().Add(r); Save();
    }
    public static void Remove(Rule r) { List().Remove(r); Save(); }

    public static int Export(string path) { File.WriteAllText(path, JsonSerializer.Serialize(List(), Opt)); return List().Count; }
    public static int Import(string path)
    {
        var src = JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(path)) ?? new(); int n = 0;
        foreach (var r in src.Where(r => (r.When ?? "").Trim().Length >= 2 && (r.Suggest ?? "").Trim().Length > 0 && r.Suggest.Trim().Length > 0))
            if (!List().Any(x => x.When == r.When && x.Suggest == r.Suggest && x.Process == r.Process)) { List().Add(r); n++; }
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
        if (_busy || st.Paused || !st.RulesOptIn) return;
        var fg = Native.Foreground();
        if (fg == null || fg.Pid == Environment.ProcessId || st.IsExcluded(fg.Process) || !For(fg.Process).Any()) return;
        _busy = true;
        try
        {
            var uia = await Task.Run(() => UiaFinder.Enumerate(fg.Hwnd));
            foreach (var r in Hits(fg.Process, uia))
            {
                var key = fg.Hwnd + "|" + r.When + "|" + r.Suggest;
                if (_cool.TryGetValue(key, out var u) && u > DateTime.Now) continue;
                if (_cool.Count > 200) foreach (var k in _cool.Where(c => c.Value < DateTime.Now).Select(c => c.Key).ToList()) _cool.Remove(k);
                _cool[key] = DateTime.Now.AddSeconds(60);
                App.Current.LastSuggestion = (r, fg);
                r.Shown++; try { Save(); } catch { }
                if (App.Current.Settings.RulesQuiet) { System.Media.SystemSounds.Asterisk.Play(); App.Current.TrayHint($"Подсказка: «{r.Suggest}»"); }
                else App.Current.Toast("Aura · подсказка", $"Вижу «{r.When}» в {fg.Process}. Предлагаю нажать «{r.Suggest}» — открой Aura ({App.Current.HotkeyName}) и нажми ▶. Сам не жму.");
                break;
            }
        }
        catch { }
        finally { _busy = false; }
    }
}
