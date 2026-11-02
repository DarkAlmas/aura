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
        try { if (File.Exists(FilePath)) _cache = JsonSerializer.Deserialize<List<LearnedButton>>(File.ReadAllText(FilePath)); } catch { }
        return _cache ??= new();
    }
    static void Save() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(List(), Opt)); }

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
        if (old != null) { old.Action = act; b = old; } else l.Add(b);
        if (template && !b.HasTemplate) b.HasTemplate = Templates.Save(b.Id, t.Bounds);
        Save(); return b;
    }
    public static void Delete(LearnedButton b) { Templates.Delete(b.Id); List().RemoveAll(x => x.Same(b)); Save(); }
    public static void Clear() { foreach (var b in List()) Templates.Delete(b.Id); List().Clear(); Save(); }
    public static void Update() => Save();
    public static LearnedButton? ByName(string name, string? process = null) => List().FirstOrDefault(b =>
        string.Equals(b.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase) && (process == null || string.Equals(b.Process, process, StringComparison.OrdinalIgnoreCase)));

    /// v0.5.0 shared «Делаю» 1 s + Стоп → act. Used by Watcher, startup actions and AI intents. Returns how, or null if stopped/aborted.
    public static async Task<string?> DoWithCountdown(UiaFinder.Target hit, ClickAction act, IntPtr hwnd, bool requireForeground = false)
    {
        var st = App.Current.Settings;
        if (st.Paused || App.Current.ActionCts != null) return null;
        var cts = new CancellationTokenSource(); App.Current.ActionCts = cts;
        try
        {
            App.Current.Look.SetDoing(hit.Label + (act == ClickAction.DoubleClick ? " ×2" : act == ClickAction.RightClick ? " (ПКМ)" : ""), stoppable: true); App.Current.Look.Show();
            await Task.Delay(1000, cts.Token);
            if (App.Current.Settings.Paused) throw new OperationCanceledException();
            if (hwnd != IntPtr.Zero && Native.GetForegroundWindow() != hwnd) { if (requireForeground) return null; Native.SetForegroundWindow(hwnd); await Task.Delay(150, cts.Token); }
            var how = await Task.Run(() => UiaFinder.Click(hit, act));
            if (App.Current.Settings.ClickSound) System.Media.SystemSounds.Asterisk.Play();
            App.Current.Toast("Aura", $"Нажал «{hit.Label}» ({how}).");
            return how;
        }
        catch (OperationCanceledException) { App.Current.Toast("Aura", "Остановлено."); return null; }
        finally { if (ReferenceEquals(App.Current.ActionCts, cts)) App.Current.ActionCts = null; App.Current.RefreshLook(); }
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
            var w = await OcrFinder.FindNames(hwnd, new[] { b.Name });
            if (w != null && w.Count > 0) return w[0];
        }
        if (b.HasTemplate)
        {
            var r = await Task.Run(() => Templates.Match(b.Id, hwnd, b.Rect));
            if (r != null) return new UiaFinder.Target(b.Name, "", "картинка", r.Value, null);
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
                    var words = await OcrFinder.FindNames(fg.Hwnd, ocrB.Select(b => b.Name));
                    if (words != null)
                        foreach (var b in ocrB) { hit = OcrFinder.Best(words, b.Name); if (hit != null) { lb = b; break; } }
                }
                if (hit == null)
                    foreach (var b in rest.Where(b => b.HasTemplate))
                    {
                        var r = await Task.Run(() => Templates.Match(b.Id, fg.Hwnd, b.Rect));
                        if (r != null) { hit = new UiaFinder.Target(b.Name, "", "картинка", r.Value, null); lb = b; break; }
                    }
            }
            if (hit == null || lb == null) return;
            _cool[fg.Hwnd + "|" + lb] = DateTime.Now.AddSeconds(15);
            if (!App.Current.Settings.ActOptIn) return;
            await Buttons.DoWithCountdown(hit, lb.Action, fg.Hwnd, requireForeground: true);
        }
        catch { }
        finally { _busy = false; App.Current.RefreshLook(); }
    }
}
