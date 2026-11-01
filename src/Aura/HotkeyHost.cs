using System;
using System.Windows.Interop;

namespace Aura;

/// Message-only window that receives WM_HOTKEY for Win+Shift+A. This is RegisterHotKey, not a keyboard hook.
internal sealed class HotkeyHost : IDisposable
{
    const int Id = 0xA0A;
    readonly HwndSource _src;
    public event Action? Pressed;
    public bool Registered { get; }

    public HotkeyHost()
    {
        var p = new HwndSourceParameters("AuraHotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
        Registered = Native.RegisterHotKey(_src.Handle, Id, Native.MOD_WIN | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x41 /* A */);
    }
    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && w.ToInt32() == Id) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Dispose() { Native.UnregisterHotKey(_src.Handle, Id); _src.Dispose(); }
}
