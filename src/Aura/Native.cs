using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Aura;

public enum ClickAction { Click, DoubleClick, RightClick }

/// Only foreground-window reads + RegisterHotKey + SendInput click. No input hooks (SetWindowsHookEx is NOT used).
public static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    const uint LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out POINT p);
    /// v0.5.3 high-DPI: physical pixels (same space as UIA BoundingRectangle / SendInput / screen capture), fallback to GetCursorPos.
    public static bool CursorPhysical(out POINT p) => GetPhysicalCursorPos(out p) || GetCursorPos(out p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    public static int PidOf(IntPtr h) { GetWindowThreadProcessId(h, out var p); return (int)p; }
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();

    static void Send(params uint[] flags)
    {
        var a = new INPUT[flags.Length];
        for (int i = 0; i < flags.Length; i++) { a[i].type = 0; a[i].mi.dwFlags = flags[i]; }
        SendInput((uint)a.Length, a, Marshal.SizeOf<INPUT>());
    }
    /// v0.5.0: one confirmed action at a point — Click / DoubleClick / RightClick (SendInput, no hooks).
    public static void ActAt(int x, int y, ClickAction act)
    {
        bool had = GetCursorPos(out var back);
        SetCursorPos(x, y);
        try { switch (act)
        {
            case ClickAction.DoubleClick: Send(LEFTDOWN, LEFTUP); System.Threading.Thread.Sleep((int)Math.Min(60, GetDoubleClickTime() / 4)); Send(LEFTDOWN, LEFTUP); break;
            case ClickAction.RightClick: Send(RIGHTDOWN, RIGHTUP); break;
            default: Send(LEFTDOWN, LEFTUP); break;
        } }
        finally { if (had) { System.Threading.Thread.Sleep(40); SetCursorPos(back.X, back.Y); } } // v0.9.12: mouse returns where the user left it
    }
    /// v0.9.14: screen capture never reads pixels of an excluded app (or Aura) overlapping the rect — 4×4 grid probe; if hit, the bitmap stays black.
    public static bool SafeCapture(System.Drawing.Graphics g, int L, int T, int w, int h)
    {
        try
        {
            var st = App.Current.Settings;
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
            {
                var wi = WindowAt(new POINT { X = L + (int)(w * (i + 0.5) / 4), Y = T + (int)(h * (j + 0.5) / 4) });
                if (wi != null && st.IsExcluded(wi.Process)) { g.Clear(System.Drawing.Color.Black); return false; }
            }
        }
        catch { }
        g.CopyFromScreen(L, T, 0, 0, new System.Drawing.Size(w, h));
        return true;
    }
    /// Root window + process under a screen point (for «Под курсором»).
    public static WindowInfo? WindowAt(POINT p)
    {
        var h = WindowFromPoint(p); if (h == IntPtr.Zero) return null;
        h = GetAncestor(h, 2); if (h == IntPtr.Zero) return null;
        var sb = new StringBuilder(GetWindowTextLength(h) + 1); GetWindowText(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out var pid); string name = "?";
        try { using var pr = System.Diagnostics.Process.GetProcessById((int)pid); name = pr.ProcessName; } catch { }
        return new WindowInfo(h, sb.ToString(), name, (int)pid);
    }

    /// One synthetic left click, only after explicit user confirmation in the overlay. Not a hook.
    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        var a = new INPUT[2];
        a[0].type = 0; a[0].mi.dwFlags = LEFTDOWN;
        a[1].type = 0; a[1].mi.dwFlags = LEFTUP;
        SendInput(2, a, Marshal.SizeOf<INPUT>());
    }

    public record WindowInfo(IntPtr Hwnd, string Title, string Process, int Pid);

    /// v0.5.3: true if the foreground window is the target or one of Aura's own windows (overlay / indicator).
    public static bool ForegroundIsTargetOrAura(IntPtr target)
    {
        var h = GetForegroundWindow();
        if (h == target || h == IntPtr.Zero) return h == target;
        GetWindowThreadProcessId(h, out var pid); return pid == (uint)Environment.ProcessId;
    }
    public static WindowInfo? Foreground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        var sb = new StringBuilder(GetWindowTextLength(h) + 1);
        GetWindowText(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out var pid);
        string name = "?";
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
        return new WindowInfo(h, sb.ToString(), name, (int)pid);
    }
}
