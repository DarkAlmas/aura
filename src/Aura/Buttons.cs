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
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"[{AutomationId}]" : Name;
    public override string ToString() => $"«{Label}» · {Type} · {Process}";

    public bool Same(LearnedButton o) => string.Equals(Process, o.Process, StringComparison.OrdinalIgnoreCase)
        && Name == o.Name && AutomationId == o.AutomationId && Type == o.Type;

    /// Prefer AutomationId+Name+Type; fallback Name+Type. Process checked by caller. Empty names never match by name alone.
    public bool Matches(UiaFinder.Target t)
    {
        if (t.Element == null || t.Type != Type) return false;
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
    public static LearnedButton? Remember(string process, UiaFinder.Target t)
    {
        if (App.Current.Settings.IsExcluded(process) || t.Element == null || string.IsNullOrWhiteSpace(t.Name)) return null;
        var b = new LearnedButton { Process = process, Name = t.Name, AutomationId = t.AutomationId, Type = t.Type };
        var l = List();
        var old = l.FirstOrDefault(x => x.Same(b));
        if (old != null) return old;
        l.Add(b); Save(); return b;
    }
    public static void Delete(LearnedButton b) { List().RemoveAll(x => x.Same(b)); Save(); }
    public static void Clear() { List().Clear(); Save(); }
}

/// v0.4.0 «Действовать» (opt-in, default OFF). Every 2 s: one UIA read of the FOREGROUND window only (no OCR, no capture).
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
        if (fg == null || fg.Pid == Environment.ProcessId || st.IsExcluded(fg.Process)) return;
        var mine = learned.Where(b => string.Equals(b.Process, fg.Process, StringComparison.OrdinalIgnoreCase)).ToList();
        App.Current.LastApp = fg.Process; App.Current.RefreshLook();
        if (mine.Count == 0) return;
        _busy = true;
        CancellationTokenSource? cts = null;
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
            if (hit == null || lb == null) return;
            _cool[fg.Hwnd + "|" + lb] = DateTime.Now.AddSeconds(15);
            cts = new CancellationTokenSource(); App.Current.ActionCts = cts;
            App.Current.Look.SetDoing(hit.Label, stoppable: true); App.Current.Look.Show();
            await Task.Delay(1000, cts.Token);
            if (App.Current.Settings.Paused || !App.Current.Settings.ActOptIn) throw new OperationCanceledException();
            if (Native.GetForegroundWindow() != fg.Hwnd) return; // user switched away — don't click
            var how = await Task.Run(() => UiaFinder.Click(hit));
            App.Current.Toast("Aura", $"Нажал «{hit.Label}» ({how}).");
        }
        catch (OperationCanceledException) { App.Current.Toast("Aura", "Остановлено."); }
        catch { }
        finally
        {
            _busy = false;
            if (cts != null && ReferenceEquals(App.Current.ActionCts, cts)) App.Current.ActionCts = null;
            App.Current.RefreshLook();
        }
    }
}
