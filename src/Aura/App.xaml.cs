using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Aura;

public partial class App : Application
{
    Mutex? _single;
    HotkeyHost? _hotkey;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _pauseItem, _ocrItem;
    public Settings Settings { get; private set; } = new();
    LookIndicator? _look;
    OverlayWindow? _overlay;
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "Aura.SingleInstance", out var first);
        if (!first) { Shutdown(); return; }
        Settings = Settings.Load();

        _tray = new Forms.NotifyIcon { Text = "Aura — Win+Shift+A", Icon = System.Drawing.SystemIcons.Information, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть (Win+Shift+A)", null, (_, _) => ShowOverlay());
        _pauseItem = new Forms.ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
        _ocrItem = new Forms.ToolStripMenuItem("Скрин/OCR (opt-in, v1 заглушка)", null, (_, _) => ToggleOcr());
        menu.Items.Add(_pauseItem); menu.Items.Add(_ocrItem);
        menu.Items.Add("Настройки (settings.json)", null, (_, _) => OpenPath(Settings.Dir));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowOverlay(); };
        SyncMenu();

        _hotkey = new HotkeyHost();
        _hotkey.Pressed += ShowOverlay;
        if (!_hotkey.Registered)
            Toast("Aura", "Win+Shift+A занят другим приложением. Открывай из трея.");
        else
            Toast("Aura", "Запущена. Win+Shift+A — подсказки.");
    }

    void ShowOverlay()
    {
        if (Settings.Paused) { Toast("Aura на паузе", "Сними паузу в трее, чтобы смотреть окно."); return; }
        // Snapshot foreground BEFORE our window takes focus. One-shot read, on user trigger only.
        var fg = Native.Foreground();
        if (fg != null && fg.Pid == Environment.ProcessId) fg = null;
        if (fg != null && Settings.IsExcluded(fg.Process)) fg = fg with { Title = "(приложение в исключениях)" };
        _look ??= new LookIndicator();
        _look.SetState(fg?.Process ?? "—", paused: false);
        _look.Show();
        _overlay?.SafeClose();
        var ov = new OverlayWindow(fg);
        _overlay = ov;
        ov.Closed += (_, _) => { if (ReferenceEquals(_overlay, ov)) { _overlay = null; _look?.Hide(); } };
        _overlay.Show(); _overlay.Activate();
    }

    public void TogglePause()
    {
        Settings.Paused = !Settings.Paused; Settings.Save(); SyncMenu();
        if (Settings.Paused) { _overlay?.SafeClose(); _look?.Hide(); }
    }
    void ToggleOcr() { Settings.ScreenCaptureOptIn = !Settings.ScreenCaptureOptIn; Settings.Save(); SyncMenu(); }
    void SyncMenu()
    {
        if (_pauseItem != null) _pauseItem.Checked = Settings.Paused;
        if (_ocrItem != null) _ocrItem.Checked = Settings.ScreenCaptureOptIn;
    }
    public void Toast(string title, string text) => _tray?.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);
    public static void OpenPath(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }
    void Quit() { _hotkey?.Dispose(); if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } Shutdown(); }
    protected override void OnExit(ExitEventArgs e) { _hotkey?.Dispose(); _tray?.Dispose(); base.OnExit(e); }
}
