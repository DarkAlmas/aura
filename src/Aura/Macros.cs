using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Aura;

/// v0.3.0 «Учусь». Records ONLY steps the user performs through Aura itself («Нажать» / авто / OCR-нажатие)
/// while the visible «Учусь» toggle is ON. No hooks, no keystrokes, no clipboard, no background screen reads.
/// Stored locally in %AppData%\Aura\macros\*.json. Playback only after per-macro «разрешить» + 1 s «Делаю» per step.
public sealed class MacroStep
{
    public string Process { get; set; } = "";
    public string Name { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string Type { get; set; } = "";
    public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; }
    public bool Ocr { get; set; }
    public string Action { get; set; } = "click";
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"[{AutomationId}]" : Name;
}

public sealed class Macro
{
    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public bool Allowed { get; set; }
    public List<MacroStep> Steps { get; set; } = new();
    public override string ToString() => $"{Name} · {Steps.Count} шаг(ов){(Allowed ? " · разрешён" : "")}";
}

public static class Learn
{
    public static Macro? Current { get; private set; }
    public static bool Recording => Current != null;
    static string Dir => Path.Combine(Settings.Dir, "macros");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static void Start() { Current = new Macro { Name = "Макрос " + DateTime.Now.ToString("dd.MM HH:mm") }; Refresh(); }

    public static Macro? Stop(bool save = true)
    {
        var m = Current; Current = null; Refresh();
        if (m == null || !save || m.Steps.Count == 0) return null;
        Save(m); return m;
    }

    /// Called only from the overlay's confirmed click path.
    public static void Add(string process, UiaFinder.Target t)
    {
        if (Current == null) return;
        if (App.Current.Settings.IsExcluded(process)) { Stop(); App.Current.Toast("Aura", "Учусь остановлен: приложение в исключениях."); return; }
        Current.Steps.Add(new MacroStep { Process = process, Name = t.Name, AutomationId = t.AutomationId, Type = t.Type,
            X = t.Bounds.X, Y = t.Bounds.Y, W = t.Bounds.Width, H = t.Bounds.Height, Ocr = t.Element == null });
        Refresh();
    }

    static void Refresh() { try { App.Current.Look.SetState(App.Current.LastApp, App.Current.Settings.Paused); if (Recording) App.Current.Look.Show(); } catch { } }

    static string FileFor(Macro m) => Path.Combine(Dir, string.Concat(m.Created.ToString("yyyyMMdd-HHmmss"), ".json"));
    public static void Save(Macro m) { Directory.CreateDirectory(Dir); File.WriteAllText(FileFor(m), JsonSerializer.Serialize(m, Opt)); }
    public static void Delete(Macro m) { try { File.Delete(FileFor(m)); } catch { } }

    public static List<Macro> List()
    {
        var r = new List<Macro>();
        try
        {
            if (!Directory.Exists(Dir)) return r;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
                try { var m = JsonSerializer.Deserialize<Macro>(File.ReadAllText(f)); if (m != null) r.Add(m); } catch { }
        }
        catch { }
        return r.OrderByDescending(m => m.Created).ToList();
    }

    /// Resolve a step to a live target: UIA by AutomationId/Name in the process's main window, else recorded bounds.
    public static (IntPtr hwnd, UiaFinder.Target? t) Resolve(MacroStep s)
    {
        var hwnd = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName(s.Process)) { if (p.MainWindowHandle != IntPtr.Zero) { hwnd = p.MainWindowHandle; break; } }
        if (hwnd == IntPtr.Zero) return (hwnd, null);
        if (!s.Ocr)
        {
            var all = UiaFinder.Enumerate(hwnd);
            var hit = all.FirstOrDefault(t => s.AutomationId.Length > 0 && t.AutomationId == s.AutomationId && t.Name == s.Name)
                   ?? all.FirstOrDefault(t => s.AutomationId.Length > 0 && t.AutomationId == s.AutomationId)
                   ?? all.FirstOrDefault(t => s.Name.Length > 0 && t.Name == s.Name);
            return (hwnd, hit);
        }
        return (hwnd, new UiaFinder.Target(s.Name, "", "OCR", new Rect(s.X, s.Y, s.W, s.H), null));
    }
}
