using System;
using System.Windows.Interop;

namespace Aura;

/// Message-only window that receives WM_HOTKEY. RegisterHotKey only — no keyboard hooks.
/// Tries combos in order: Win+Shift+Z → Win+Ctrl+A → Win+Alt+Z.
internal sealed class HotkeyHost : IDisposable
{
    const int Id = 0xA0A;
    static readonly (uint Mods, uint Vk, string Name)[] Combos =
    {
        (Native.MOD_WIN | Native.MOD_SHIFT,   0x5A, "Win+Shift+Z"),
        (Native.MOD_WIN | Native.MOD_CONTROL, 0x41, "Win+Ctrl+A"),
        (Native.MOD_WIN | Native.MOD_ALT,     0x5A, "Win+Alt+Z"),
    };
    readonly HwndSource _src;
    public event Action? Pressed;
    public bool Registered => Combo != null;
    public string? Combo { get; }

    public HotkeyHost()
    {
        var p = new HwndSourceParameters("AuraHotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
        foreach (var c in Combos)
            if (Native.RegisterHotKey(_src.Handle, Id, c.Mods | Native.MOD_NOREPEAT, c.Vk)) { Combo = c.Name; break; }
    }
    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && w.ToInt32() == Id) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Dispose() { if (Registered) Native.UnregisterHotKey(_src.Handle, Id); _src.Dispose(); }
}
