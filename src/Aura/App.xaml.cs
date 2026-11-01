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
    EventWaitHandle? _openEvent;
    volatile bool _quitting;
    string _combo = "трей";
    /// External tools can Set this named event (or run `Aura.exe --open`) to open the overlay.
    public const string OpenEventName = "Local\\Aura.OpenOverlay";
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "Aura.SingleInstance", out var first);
        if (!first)
        {
            // Second instance (e.g. `Aura.exe --open`): ask the running one to open the overlay.
            try { using var ev = EventWaitHandle.OpenExisting(OpenEventName); ev.Set(); } catch { }
            Shutdown(); return;
        }
        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
        var t = new Thread(() => { while (_openEvent.WaitOne() && !_quitting) RequestOverlay(); }) { IsBackground = true, Name = "AuraOpenEvent" };
        t.Start();
        Settings = Settings.Load();

        _tray = new Forms.NotifyIcon { Text = "Aura — Win+Shift+Z", Icon = System.Drawing.SystemIcons.Information, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        var openItem = menu.Items.Add("Открыть (Win+Shift+Z)", null, (_, _) => RequestOverlay());
        _pauseItem = new Forms.ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
        _ocrItem = new Forms.ToolStripMenuItem("Скрин/OCR (opt-in, заглушка)", null, (_, _) => ToggleOcr());
        menu.Items.Add(_pauseItem); menu.Items.Add(_ocrItem);
        menu.Items.Add("Настройки (settings.json)", null, (_, _) => OpenPath(Settings.Dir));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) RequestOverlay(); };
        SyncMenu();

        _hotkey = new HotkeyHost();
        _hotkey.Pressed += RequestOverlay;
        if (!_hotkey.Registered)
            Toast("Aura", "Все хоткеи заняты (Win+Shift+Z, Win+Ctrl+A, Win+Alt+Z). Открывай кликом по иконке в трее.");
        else
        {
            _combo = _hotkey.Combo!;
            Toast("Aura", $"Запущена. {_combo} — подсказки (или клик по трею).");
        }
        _tray.Text = $"Aura — {_combo}";
        openItem.Text = $"Открыть ({_combo})";
        if (Array.Exists(e.Args, a => a == "--open")) RequestOverlay();
    }

    /// Cancel source for the in-flight «Делаю» action; Pause/Stop/Esc cancel it.
    public System.Threading.CancellationTokenSource? ActionCts;
    public LookIndicator Look => _look ??= new LookIndicator();
    public string HotkeyName => _combo;
    /// Always marshal to the WPF UI thread, async so we never run inside tray/activation callbacks.
    void RequestOverlay() => Dispatcher.BeginInvoke(new Action(ShowOverlay));

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
        if (Settings.Paused) ActionCts?.Cancel();
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
    void Quit() { _quitting = true; _openEvent?.Set(); _hotkey?.Dispose(); if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } Shutdown(); }
    protected override void OnExit(ExitEventArgs e) { _hotkey?.Dispose(); _tray?.Dispose(); base.OnExit(e); }
}
