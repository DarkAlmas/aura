using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Aura;

/// v0.2.1: temporary on-screen frame over the selected UIA target's BoundingRectangle.
/// Click-through (WS_EX_TRANSPARENT), never activates (WS_EX_NOACTIVATE), no capture, no hooks.
/// Lives only while the overlay is open; hidden on Esc / Стоп / close / clear selection.
public sealed class TargetHighlight : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

    const int Pad = 4;
    readonly Border _frame;
    IntPtr _hwnd;

    public TargetHighlight()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Focusable = false; ResizeMode = ResizeMode.NoResize;
        Left = -10000; Top = -10000; Width = 10; Height = 10;
        _frame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x1A)), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x8C, 0x1A)), IsHitTestVisible = false
        };
        Content = _frame;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        };
    }

    /// Bounds are UIA physical screen pixels; position via SetWindowPos in pixels (no DIP conversion).
    public void ShowAt(Rect r)
    {
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) { Hide(); return; }
        if (!IsVisible) Show();
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, (int)r.X - Pad, (int)r.Y - Pad, (int)r.Width + Pad * 2, (int)r.Height + Pad * 2, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
