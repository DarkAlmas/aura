using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;

namespace Aura;

/// v0.2 «Найти и нажать»: one-shot UI Automation read of the foreground window, on user trigger only.
/// No hooks, no screenshots, nothing leaves the PC.
public static class UiaFinder
{
    public sealed record Target(string Name, string AutomationId, string Type, Rect Bounds, AutomationElement? Element)
    {
        public string Label => string.IsNullOrWhiteSpace(Name) ? $"[{AutomationId}]" : Name;
        public override string ToString() => $"{Label} · {Type} · {(int)Bounds.X},{(int)Bounds.Y} {(int)Bounds.Width}×{(int)Bounds.Height}";
    }

    static readonly ControlType[] Clickable =
    {
        ControlType.Button, ControlType.Hyperlink, ControlType.MenuItem, ControlType.TabItem,
        ControlType.CheckBox, ControlType.RadioButton, ControlType.ListItem, ControlType.SplitButton, ControlType.TreeItem
    };

    public static readonly HashSet<ControlType> ClickableSet = new(Clickable);

    /// v0.5.0 «Под курсором»: UIA element at a screen point; walks up to the nearest clickable named ancestor.
    public static Target? AtPoint(Point p)
    {
        try
        {
            var el = AutomationElement.FromPoint(p);
            for (int i = 0; el != null && i < 6; i++)
            {
                var c = el.Current;
                if (ClickableSet.Contains(c.ControlType) && !string.IsNullOrWhiteSpace(c.Name) && !c.BoundingRectangle.IsEmpty)
                    return new Target(c.Name, c.AutomationId ?? "", c.LocalizedControlType ?? "", c.BoundingRectangle, el);
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
        }
        catch { }
        return null;
    }

    static int _inflight;
    /// v0.9.36: UIA read with a 6 s timeout — a hung app can no longer freeze the watcher/rules forever (they just get an empty list).
    public static async System.Threading.Tasks.Task<List<Target>> EnumerateAsync(IntPtr hwnd)
    {
        if (System.Threading.Volatile.Read(ref _inflight) >= 3) return new();
        System.Threading.Interlocked.Increment(ref _inflight);
        var t = System.Threading.Tasks.Task.Run(() => { try { return Enumerate(hwnd); } catch { return new List<Target>(); } finally { System.Threading.Interlocked.Decrement(ref _inflight); } });
        var done = await System.Threading.Tasks.Task.WhenAny(t, System.Threading.Tasks.Task.Delay(6000));
        return done == t ? t.Result : new();
    }
    public static List<Target> Enumerate(IntPtr hwnd, int max = 400)
    {
        var res = new List<Target>();
        if (hwnd == IntPtr.Zero) return res;
        var root = AutomationElement.FromHandle(hwnd);
        System.Windows.Automation.Condition[] conds = new System.Windows.Automation.Condition[Clickable.Length];
        for (int i = 0; i < Clickable.Length; i++) conds[i] = new PropertyCondition(AutomationElement.ControlTypeProperty, Clickable[i]);
        var cond = new AndCondition(new OrCondition(conds),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true));
        var all = root.FindAll(TreeScope.Descendants, cond);
        foreach (AutomationElement el in all)
        {
            if (res.Count >= max) break;
            try
            {
                var c = el.Current;
                if (string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.AutomationId)) continue;
                var r = c.BoundingRectangle;
                if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
                res.Add(new Target(c.Name ?? "", c.AutomationId ?? "", c.LocalizedControlType ?? "", r, el));
            }
            catch (ElementNotAvailableException) { }
        }
        return res;
    }

    /// Case-insensitive contains on Name/AutomationId (works for Russian). Exact matches first.
    public static List<Target> Match(List<Target> all, string q)
    {
        q = q.Trim();
        if (q.Length == 0) return all;
        var exact = new List<Target>(); var part = new List<Target>();
        foreach (var t in all)
        {
            if (string.Equals(t.Name.Trim(), q, StringComparison.CurrentCultureIgnoreCase)) exact.Add(t);
            else if (t.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.AutomationId.Contains(q, StringComparison.OrdinalIgnoreCase)) part.Add(t);
        }
        exact.AddRange(part); return exact;
    }

    /// Single click on one confirmed target: Invoke/Toggle/Select pattern, else one SendInput click at its clickable point.
    /// v0.9.37: in-memory log of real presses this session (never written to disk).
    public static readonly List<string> Log = new();
    public static string Click(Target t, ClickAction act = ClickAction.Click, int expectPid = 0)
    {
        var r = ClickCore(t, act, expectPid);
        if (!r.StartsWith("не нажал")) lock (Log) { var n = t.Name ?? ""; Log.Add($"{System.DateTime.Now:HH:mm:ss} «{(n.Length > 40 ? n[..40] + "…" : n)}» ({r})"); if (Log.Count > 30) Log.RemoveAt(0); }
        return r;
    }
    static string ClickCore(Target t, ClickAction act, int expectPid)
    {
        var el = t.Element;
        string an = act == ClickAction.DoubleClick ? "двойной" : act == ClickAction.RightClick ? "правый" : "мышь";
        if (el == null) { if (CoveredBy((int)(t.Bounds.X + t.Bounds.Width / 2), (int)(t.Bounds.Y + t.Bounds.Height / 2), expectPid) is string c1) return c1; Native.ActAt((int)(t.Bounds.X + t.Bounds.Width / 2), (int)(t.Bounds.Y + t.Bounds.Height / 2), act); return an + (t.Type == "OCR" ? " (OCR)" : " (картинка)"); }
        if (act == ClickAction.Click) try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); return "Invoke"; }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out p)) { ((TogglePattern)p).Toggle(); return "Toggle"; }
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) { ((SelectionItemPattern)p).Select(); return "Select"; }
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p)) { ((ExpandCollapsePattern)p).Expand(); return "Expand"; }
        }
        catch (Exception) { /* fall back to mouse */ }
        Point pt;
        try { if (!el.TryGetClickablePoint(out pt)) pt = new Point(t.Bounds.X + t.Bounds.Width / 2, t.Bounds.Y + t.Bounds.Height / 2); }
        catch { pt = new Point(t.Bounds.X + t.Bounds.Width / 2, t.Bounds.Y + t.Bounds.Height / 2); }
        if (CoveredBy((int)pt.X, (int)pt.Y, expectPid) is string c2) return c2;
        Native.ActAt((int)pt.X, (int)pt.Y, act);
        return an;
    }
    /// v0.9.11: before a coordinate click, check what is REALLY under that point — never click into an excluded app or Aura itself (e.g. a popup covered the button).
    static string? CoveredBy(int x, int y, int expectPid)
    {
        try
        {
            var w = Native.WindowAt(new Native.POINT { X = x, Y = y });
            if (w == null) return null;
            if (w.Pid == System.Environment.ProcessId) return "не нажал: точку закрывает окно Aura";
            if (App.Current.Settings.IsExcluded(w.Process)) return $"не нажал: точку закрывает {w.Process} (исключение)";
            if (expectPid != 0 && w.Pid != expectPid) return $"не нажал: точку закрывает другое окно ({w.Process})"; // v0.9.29
        }
        catch { }
        return null;
    }
}
