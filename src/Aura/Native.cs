using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Aura;

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
    const uint LEFTDOWN = 0x2, LEFTUP = 0x4;

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
