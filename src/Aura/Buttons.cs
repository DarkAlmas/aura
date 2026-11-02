using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Aura;

/// v0.4.0 learned button. Saved ONLY when the user explicitly picks a control in Aura («Запомнить», or «Нажать» while «Учусь» is ON).
/// No hooks (SetWindowsHookEx is NOT used), no keystrokes, no clipboard, no screenshots. Local file only.
public sealed class LearnedButton
{
    public string Process { get; set; } = "";
    public string Name { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public bool Ocr { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Rect? Rect => W > 0 && H > 0 ? new System.Windows.Rect(X, Y, W, H) : null;
    /// v0.5.0: Click / DoubleClick / RightClick.
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public ClickAction Action { get; set; } = ClickAction.Click;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// v0.5.1: a small template PNG exists in %AppData%\Aura\templates\{Id}.png (only if user ticked «запомнить картинку»).
    public bool HasTemplate { get; set; }
    /// v0.5.2: «действовать» uses only enabled buttons (toggle «вкл/выкл» in the list).
    public bool Enabled { get; set; } = true;
    string ActLabel => Action == ClickAction.DoubleClick ? " · ×2" : Action == ClickAction.RightClick ? " · ПКМ" : "";
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"[{AutomationId}]" : Name;
    public override string ToString() => (Ocr ? $"«{Label}» · OCR · {Process}" : $"«{Label}» · {Type} · {Process}") + ActLabel + (HasTemplate ? " · 🖼" : "") + (Enabled ? "" : " · выкл");

    public bool Same(LearnedButton o) => string.Equals(Process, o.Process, StringComparison.OrdinalIgnoreCase)
        && Name == o.Name && AutomationId == o.AutomationId && Type == o.Type && Ocr == o.Ocr;

    /// Prefer AutomationId+Name+Type; fallback Name+Type. Process checked by caller. Empty names never match by name alone.
    public bool Matches(UiaFinder.Target t)
    {
        if (Ocr || t.Element == null || t.Type != Type) return false;
        if (AutomationId.Length > 0 && t.AutomationId == AutomationId && t.Name == Name) return true;
        return Name.Trim().Length > 0 && t.Name == Name && (AutomationId.Length == 0 || t.AutomationId.Length == 0 || t.AutomationId == AutomationId);
    }
}

public static class Buttons
{
    static string FilePath => Path.Combine(Settings.Dir, "buttons", "buttons.json");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    static List<LearnedButton>? _cache;

    public static List<LearnedButton> List()
    {
        if (_cache != null) return _cache;
        _cache = Settings.LoadJson<List<LearnedButton>>(FilePath);
        return _cache ??= new();
    }
    static void Save() => Settings.AtomicWrite(FilePath, JsonSerializer.Serialize(List(), Opt));

    /// Returns null if not saved (excluded app, OCR hit, or empty name).
    public static LearnedButton? Remember(string process, UiaFinder.Target t, ClickAction act = ClickAction.Click, bool template = false)
    {
        if (App.Current.Settings.IsExcluded(process) || string.IsNullOrWhiteSpace(t.Name)) return null;
        bool ocr = t.Element == null;
        if (ocr && t.Type != "OCR") return null;
        // OCR: only name + process + bounds are stored — never an image.
        var b = new LearnedButton { Process = process, Name = t.Name.Trim(), AutomationId = t.AutomationId, Type = t.Type, Ocr = ocr,
            X = t.Bounds.X, Y = t.Bounds.Y, W = t.Bounds.Width, H = t.Bounds.Height, Action = act };
        var l = List();
        var old = l.FirstOrDefault(x => x.Same(b));
        if (old == null && (l.Count >= 500 || b.Name.Length > 80)) { App.Current.Toast("Aura", l.Count >= 500 ? "Уже 500 кнопок — забудьте лишние." : "Слишком длинное имя кнопки — не запоминаю."); return null; } // v0.9.17: bounded
        if (old != null) { old.Action = act; b = old; } else l.Add(b);
        // v0.9.8 (Formal INFO): OCR hits/buttons NEVER get an image; a flag without a file on disk is re-created (or cleared)
        if (ocr || b.Ocr) { if (b.HasTemplate || Templates.Exists(b.Id)) Templates.Delete(b.Id); b.HasTemplate = false; }
        else
        {
            if (b.HasTemplate && !Templates.Exists(b.Id)) b.HasTemplate = false;
            if (template && !b.HasTemplate) b.HasTemplate = Templates.Save(b.Id, t.Bounds);
        }
        Save(); return b;
    }
    /// v0.9.9 startup hygiene: OCR buttons hold no image; flags match files on disk; orphan PNGs (button forgotten) are removed.
    public static void TemplateHygiene()
    {
        try
        {
            bool dirty = false;
            foreach (var b in List())
            {
                if (b.Ocr && (b.HasTemplate || Templates.Exists(b.Id))) { Templates.Delete(b.Id); b.HasTemplate = false; dirty = true; }
                else if (b.HasTemplate && !Templates.Exists(b.Id)) { b.HasTemplate = false; dirty = true; }
            }
            if (Directory.Exists(Templates.Dir))
            {
                var keep = new HashSet<string>(List().Where(b => b.HasTemplate).Select(b => b.Id + ".png"), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(Templates.Dir, "*.png")) if (!keep.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch { }
            }
            if (dirty) Save();
        }
        catch { }
    }
    /// v0.9.10: wipe every saved button image (buttons stay; they fall back to UIA/OCR).
    public static int DeleteAllTemplates()
    {
        int n = 0;
        foreach (var b in List()) { if (b.HasTemplate || Templates.Exists(b.Id)) { Templates.Delete(b.Id); n++; } b.HasTemplate = false; }
        try { if (Directory.Exists(Templates.Dir)) foreach (var f in Directory.GetFiles(Templates.Dir, "*.png")) { File.Delete(f); } } catch { }
        Save(); return n;
    }
    public static void Delete(LearnedButton b) { Templates.Delete(b.Id); List().RemoveAll(x => x.Same(b)); Save(); }
    /// v0.5.4: export/import learned buttons as JSON (names, actions, positions only — no images, no screenshots).
    public static int Export(string path) { File.WriteAllText(path, JsonSerializer.Serialize(List().Select(b => { var c = JsonSerializer.Deserialize<LearnedButton>(JsonSerializer.Serialize(b, Opt), Opt)!; c.HasTemplate = false; return c; }).ToList(), Opt)); return List().Count; }
    public static int Import(string path)
    {
        // v0.7.6 validation: ≤1 MB, ≤300 items, sane names/process/bounds; imported buttons arrive DISABLED so «действовать» never presses them until you enable each one
        if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException("файл больше 1 МБ");
        var src = JsonSerializer.Deserialize<List<LearnedButton>>(File.ReadAllText(path), Opt) ?? new(); int n = 0;
        static bool Ok(string? t, int max) => !string.IsNullOrWhiteSpace(t) && t.Length <= max && !t.Any(char.IsControl);
        foreach (var b in src.Take(300).Where(b => Ok(b.Name, 120) && Ok(b.Process, 64) && b.Process.IndexOfAny(new[] { '\\', '/', ':' }) < 0
                                                   && (b.AutomationId ?? "").Length <= 200 && b.W >= 0 && b.H >= 0 && b.W < 10000 && b.H < 10000))
        {
            if (List().Any(x => x.Same(b))) continue;
            b.HasTemplate = false; b.Enabled = false; b.Id = Guid.NewGuid().ToString("N")[..12]; List().Add(b); n++;
        }
        Save(); return n;
    }
    public static void Clear() { foreach (var b in List()) Templates.Delete(b.Id); List().Clear(); Save(); }
    public static void Update() => Save();
    public static LearnedButton? ByName(string name, string? process = null) => List().FirstOrDefault(b =>
        string.Equals(b.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase) && (process == null || string.Equals(b.Process, process, StringComparison.OrdinalIgnoreCase)));

    /// v0.5.0 shared «Делаю» 1 s + Стоп → act. Used by Watcher, startup actions and AI intents. Returns how, or null if stopped/aborted.
    public static async Task<string?> DoWithCountdown(UiaFinder.Target hit, ClickAction act, IntPtr hwnd, bool requireForeground = false, bool guardLeft = false)
    {
        var st = App.Current.Settings;
        if (st.Paused || App.Current.ActionCts != null) return null;
        var cts = new CancellationTokenSource(); App.Current.ActionCts = cts;
        TargetHighlight? hl = null; // v0.9.2: frame what will be pressed during the 1 s countdown
        try
        {
            try { if (hit.Bounds.Width > 0 && hit.Bounds.Height > 0) { hl = new TargetHighlight(); hl.ShowAt(hit.Bounds); } } catch { }
            App.Current.Look.SetDoing(hit.Label + (act == ClickAction.DoubleClick ? " ×2" : act == ClickAction.RightClick ? " (ПКМ)" : ""), stoppable: true); App.Current.Look.Show();
            await Task.Delay(1000, cts.Token);
            if (App.Current.Settings.Paused) throw new OperationCanceledException();
            if (guardLeft && hwnd != IntPtr.Zero && !Native.ForegroundIsTargetOrAura(hwnd))
            { App.Current.Toast("Aura", $"Ты ушёл из окна — «{hit.Label}» не нажимаю."); return null; }
            if (hwnd != IntPtr.Zero && Native.GetForegroundWindow() != hwnd) { if (requireForeground) return null; Native.SetForegroundWindow(hwnd); await Task.Delay(150, cts.Token); }
            try { hl?.Close(); hl = null; } catch { }
            var how = await Task.Run(() => UiaFinder.Click(hit, act));
            if (App.Current.Settings.ClickSound) System.Media.SystemSounds.Asterisk.Play();
            App.Current.Toast("Aura", how.StartsWith("не нажал") ? $"«{hit.Label}»: {how}." : $"Нажал «{hit.Label}» ({how})."); // v0.9.13
            return how;
        }
        catch (OperationCanceledException) { App.Current.Toast("Aura", "Остановлено."); return null; }
        finally { try { hl?.Close(); } catch { } if (ReferenceEquals(App.Current.ActionCts, cts)) App.Current.ActionCts = null; App.Current.RefreshLook(); }
    }

    /// Locate a learned button in a window: UIA → (opt-in) OCR by its exact name → (opt-in) template. No hooks.
    public static async Task<UiaFinder.Target?> Locate(LearnedButton b, IntPtr hwnd, List<UiaFinder.Target>? uia = null)
    {
        uia ??= await Task.Run(() => UiaFinder.Enumerate(hwnd));
        var hit = uia.FirstOrDefault(b.Matches);
        if (hit != null) return hit;
        var st = App.Current.Settings;
        if (!st.ScreenCaptureOptIn || st.Paused) return null;
        if (b.Ocr && OcrFinder.Available)
        {
            using (App.Current.Scan($"текст «{b.Name}»"))
            {
                var w = await OcrFinder.FindNames(hwnd, new[] { b.Name });
                if (w != null && w.Count > 0) return w[0];
            }
        }
        if (b.HasTemplate)
        {
            using (App.Current.Scan($"картинка «{b.Name}»"))
            {
                var r = await Task.Run(() => Templates.Match(b.Id, hwnd, b.Rect));
                if (r != null) return new UiaFinder.Target(b.Name, "", "картинка", r.Value, null);
            }
        }
        return null;
    }
}

/// v0.4.1 «Действовать» (opt-in, default OFF). Every 2 s: one UIA read of the FOREGROUND window. Only if UIA found nothing AND «OCR если UIA не нашёл» is ON AND an OCR-learned button exists for this app: one in-memory capture + OCR (never saved).
/// A learned match → «Делаю» 1 s on the visible indicator (Стоп / Esc in overlay / Пауза) → one click. 15 s cooldown per button+window.
public static class Watcher
{
    static DispatcherTimer? _t;
    static bool _busy;
    static readonly Dictionary<string, DateTime> _cool = new();

    public static void Sync()
    {
        var s = App.Current.Settings;
        bool on = s.ActOptIn && !s.Paused;
        if (on && _t == null) { _t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; _t.Tick += Tick; _t.Start(); }
        if (!on && _t != null) { _t.Stop(); _t = null; App.Current.ActionCts?.Cancel(); }
        App.Current.RefreshLook();
    }
    public static bool On => _t != null;

    static async void Tick(object? s, EventArgs e)
    {
        var st = App.Current.Settings;
        if (_busy || st.Paused || !st.ActOptIn || App.Current.ActionCts != null) return;
        var learned = Buttons.List();
        if (learned.Count == 0) return;
        var fg = Native.Foreground();
        if (fg == null || fg.Pid == Environment.ProcessId || st.IsExcluded(fg.Process) || st.ActDisabled(fg.Process)) return;
        var mine = learned.Where(b => b.Enabled && string.Equals(b.Process, fg.Process, StringComparison.OrdinalIgnoreCase)).ToList();
        App.Current.LastApp = fg.Process; App.Current.RefreshLook();
        if (mine.Count == 0) return;
        _busy = true;
        try
        {
            var all = await Task.Run(() => UiaFinder.Enumerate(fg.Hwnd));
            UiaFinder.Target? hit = null; LearnedButton? lb = null;
            foreach (var b in mine)
            {
                var key = fg.Hwnd + "|" + b;
                if (_cool.TryGetValue(key, out var until) && until > DateTime.Now) continue;
                hit = all.FirstOrDefault(b.Matches); if (hit != null) { lb = b; break; }
            }
            if (hit == null && st.ScreenCaptureOptIn)
            {   // Formal: only with «действовать» + opt-in, only learned names (exact), ONE in-memory capture; then templates if any.
                var rest = mine.Where(b => !(_cool.TryGetValue(fg.Hwnd + "|" + b, out var u) && u > DateTime.Now)).ToList();
                var ocrB = rest.Where(b => b.Ocr).ToList();
                if (ocrB.Count > 0 && OcrFinder.Available)
                {
                    List<UiaFinder.Target>? words; using (App.Current.Scan("текст")) words = await OcrFinder.FindNames(fg.Hwnd, ocrB.Select(b => b.Name));
                    if (words != null)
                        foreach (var b in ocrB) { hit = OcrFinder.Best(words, b.Name); if (hit != null) { lb = b; break; } }
                }
                if (hit == null)
                    foreach (var b in rest.Where(b => b.HasTemplate))
                    {
                        System.Windows.Rect? r; using (App.Current.Scan($"картинка «{b.Name}»")) r = await Task.Run(() => Templates.Match(b.Id, fg.Hwnd, b.Rect));
                        if (r != null) { hit = new UiaFinder.Target(b.Name, "", "картинка", r.Value, null); lb = b; break; }
                    }
            }
            if (hit == null || lb == null) return;
            _cool[fg.Hwnd + "|" + lb] = DateTime.Now.AddSeconds(15);
            if (_cool.Count > 300) foreach (var k in _cool.Where(c => c.Value < DateTime.Now).Select(c => c.Key).ToList()) _cool.Remove(k); // v0.9.1: bounded
            // v0.9.1: the scan took time — re-check pause / Стоп / exclusions / foreground right before the countdown
            if (!st.ActOptIn || st.Paused || App.Current.ActionCts != null || st.IsExcluded(fg.Process) || st.ActDisabled(fg.Process) || Native.Foreground()?.Hwnd != fg.Hwnd) return;
            await Buttons.DoWithCountdown(hit, lb.Action, fg.Hwnd, requireForeground: true);
        }
        catch { }
        finally { _busy = false; App.Current.RefreshLook(); }
    }
}
