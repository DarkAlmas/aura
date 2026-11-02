using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Aura;

public partial class App : Application
{
    Mutex? _single;
    HotkeyHost? _hotkey, _hotkeyX;
    public string UnderCursorHotkey = "—";
    public (Rule Rule, Native.WindowInfo Fg)? LastSuggestion;
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _pauseItem, _ocrItem, _actItem, _aiItem;
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
        _ocrItem = new Forms.ToolStripMenuItem("OCR если UIA не нашёл (opt-in)", null, (_, _) => ToggleOcr());
        _actItem = new Forms.ToolStripMenuItem("Действовать (сам жму выученные)", null, (_, _) => { Settings.ActOptIn = !Settings.ActOptIn; Settings.Save(); SyncMenu(); Watcher.Sync(); });
        _aiItem = new Forms.ToolStripMenuItem("ИИ (сеть, opt-in)", null, (_, _) => { Settings.AiOptIn = !Settings.AiOptIn; Settings.Save(); SyncMenu(); });
        menu.Items.Add(_pauseItem); menu.Items.Add(_ocrItem); menu.Items.Add(_actItem); menu.Items.Add(_aiItem);
        menu.Items.Add("Запомнить под курсором (через 3 с)", null, (_, _) => RememberUnderCursor(3));
        menu.Items.Add("⏹ Стоп всё (действие/серия)", null, (_, _) => Actions.Stop());
        menu.Items.Add("Экспорт кнопок…", null, (_, _) =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { FileName = "aura-buttons.json", Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) try { Toast("Aura", $"Экспортировано: {Buttons.Export(d.FileName)} (без картинок)."); } catch (Exception ex) { Toast("Aura", "Ошибка экспорта: " + ex.Message); }
        });
        menu.Items.Add("Импорт кнопок…", null, (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) try { Toast("Aura", $"Импортировано новых: {Buttons.Import(d.FileName)}."); _overlay?.Reload(); } catch (Exception ex) { Toast("Aura", "Ошибка импорта: " + ex.Message); }
        });
        menu.Items.Add("Настройки (settings.json)", null, (_, _) => OpenPath(Settings.Dir));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) RequestOverlay(); };
        SyncMenu();
        Watcher.Sync(); Rules.Sync();

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
        _hotkeyX = new HotkeyHost(0xA0B, HotkeyHost.UnderCursorCombos);
        if (_hotkeyX.Registered) { UnderCursorHotkey = _hotkeyX.Combo!; _hotkeyX.Pressed += () => Dispatcher.BeginInvoke(new Action(() => RememberUnderCursor(0))); }
        if (Array.Exists(e.Args, a => a == "--open")) RequestOverlay();
        _ = Actions.RunStartup();
    }

    /// Cancel source for the in-flight «Делаю» action; Pause/Stop/Esc cancel it.
    public System.Threading.CancellationTokenSource? ActionCts;
    public string LastApp = "—";
    /// «Учусь» — runtime only, visible (red dot). While ON, every Aura «Нажать» also remembers the button. No hooks.
    public bool Learning;
    /// Indicator stays visible while overlay is open, «Учусь» is ON, or «Действовать» is watching.
    public void RefreshLook()
    {
        try
        {
            if (ActionCts != null) return; // «Делаю» owns the label
            Look.SetState(LastApp, Settings.Paused);
            if (_overlay != null || Learning || Watcher.On) Look.Show(); else Look.Hide();
        }
        catch { }
    }
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
        LastApp = fg?.Process ?? "—";
        _look.SetState(LastApp, paused: false);
        _look.Show();
        _overlay?.SafeClose();
        var ov = new OverlayWindow(fg);
        _overlay = ov;
        ov.Closed += (_, _) => { if (ReferenceEquals(_overlay, ov)) { _overlay = null; RefreshLook(); } };
        _overlay.Show(); _overlay.Activate();
    }

    public void TogglePause()
    {
        Settings.Paused = !Settings.Paused; Settings.Save(); SyncMenu();
        if (Settings.Paused) ActionCts?.Cancel();
        if (Settings.Paused) { Learning = false; _overlay?.SafeClose(); }
        Watcher.Sync(); Rules.Sync();
    }
    void ToggleOcr() { Settings.ScreenCaptureOptIn = !Settings.ScreenCaptureOptIn; Settings.Save(); SyncMenu(); }
    public void SyncOcrMenu() => SyncMenu();
    void SyncMenu()
    {
        if (_pauseItem != null) _pauseItem.Checked = Settings.Paused;
        if (_ocrItem != null) _ocrItem.Checked = Settings.ScreenCaptureOptIn;
        if (_actItem != null) _actItem.Checked = Settings.ActOptIn;
        if (_aiItem != null) _aiItem.Checked = Settings.AiOptIn;
    }
    public void Toast(string title, string text) => _tray?.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);
    public static void OpenPath(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }
    /// v0.5.0 «Под курсором» (no hooks): read the control under the mouse ONCE, on hotkey/button press → remember it.
    public async void RememberUnderCursor(int delaySec)
    {
        if (Settings.Paused) { Toast("Aura", "Пауза — не учусь."); return; }
        if (delaySec > 0) { Toast("Aura · Под курсором", $"Наведи мышь на кнопку — запомню через {delaySec} с."); await System.Threading.Tasks.Task.Delay(delaySec * 1000); }
        if (!Native.CursorPhysical(out var p)) return;
        var w = Native.WindowAt(p);
        if (w == null || w.Pid == Environment.ProcessId) { Toast("Aura", "Под курсором нет чужого окна."); return; }
        if (Settings.IsExcluded(w.Process)) { Toast("Aura", "Приложение в исключениях — не запоминаю."); return; }
        Look.SetDoing("Учусь: под курсором"); Look.Show();
        try
        {
            var t = await System.Threading.Tasks.Task.Run(() => UiaFinder.AtPoint(new System.Windows.Point(p.X, p.Y)));
            if (t == null && Settings.ScreenCaptureOptIn && OcrFinder.Available) t = await OcrFinder.AtPoint(p.X, p.Y);
            if (t == null) { Toast("Aura", Settings.ScreenCaptureOptIn ? "Под курсором не нашёл кнопку с именем." : "UIA не нашёл кнопку. Включи «OCR если UIA не нашёл» — тогда прочитаю текст под курсором."); return; }
            var b = Buttons.Remember(w.Process, t, Settings.DefaultAction, Settings.SaveTemplates);
            if (b == null) { Toast("Aura", "Не запомнил (пустое имя?)."); return; }
            try { var hl = new TargetHighlight(); hl.ShowAt(t.Bounds); _ = Dispatcher.BeginInvoke(new Action(async () => { await System.Threading.Tasks.Task.Delay(1200); hl.Close(); })); } catch { }
            Toast("Aura · Запомнил", $"{b} ");
            _overlay?.Reload();
        }
        finally { RefreshLook(); }
    }
    void Quit() { _quitting = true; _openEvent?.Set(); _hotkey?.Dispose(); _hotkeyX?.Dispose(); if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } Shutdown(); }
    protected override void OnExit(ExitEventArgs e) { _hotkey?.Dispose(); _hotkeyX?.Dispose(); _tray?.Dispose(); base.OnExit(e); }
}
