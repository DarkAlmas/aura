using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;

namespace Aura;

/// v0.2 «Найти и нажать»: one-shot UI Automation read of the foreground window, on user trigger only.
/// No hooks, no screenshots, nothing leaves the PC.
public static class UiaFinder
{
    public sealed record Target(string Name, string AutomationId, string Type, Rect Bounds, AutomationElement Element)
    {
        public string Label => string.IsNullOrWhiteSpace(Name) ? $"[{AutomationId}]" : Name;
        public override string ToString() => $"{Label} · {Type} · {(int)Bounds.X},{(int)Bounds.Y} {(int)Bounds.Width}×{(int)Bounds.Height}";
    }

    static readonly ControlType[] Clickable =
    {
        ControlType.Button, ControlType.Hyperlink, ControlType.MenuItem, ControlType.TabItem,
        ControlType.CheckBox, ControlType.RadioButton, ControlType.ListItem, ControlType.SplitButton, ControlType.TreeItem
    };

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
    public static string Click(Target t)
    {
        var el = t.Element;
        try
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
        Native.ClickAt((int)pt.X, (int)pt.Y);
        return "мышь";
    }
}
