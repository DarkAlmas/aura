using System.Linq;
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
        _tray.BalloonTipClicked += (_, _) => OnBalloonClicked();
        DispatcherUnhandledException += (_, ev) => { CrashLog(ev.Exception); ev.Handled = true; try { Actions.Stop(); Toast("Aura", "Ошибка — записал в crash.log, продолжаю."); } catch { } };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => CrashLog(ev.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) => { CrashLog(ev.Exception); ev.SetObserved(); };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"Aura {Version}") { Enabled = false });
        menu.Items.Add("📄 Журнал ошибок", null, (_, _) => { var p = System.IO.Path.Combine(Aura.Settings.Dir, "crash.log"); if (System.IO.File.Exists(p)) OpenPath(p); else Toast("Aura", "Ошибок не было."); });
        var openItem = menu.Items.Add("Открыть (Win+Shift+Z)", null, (_, _) => RequestOverlay());
        _pauseItem = new Forms.ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
        _ocrItem = new Forms.ToolStripMenuItem("OCR если UIA не нашёл (opt-in)", null, (_, _) => ToggleOcr());
        _actItem = new Forms.ToolStripMenuItem("Действовать (сам жму выученные)", null, (_, _) => { Settings.ActOptIn = !Settings.ActOptIn; Settings.Save(); SyncMenu(); Watcher.Sync(); });
        _aiItem = new Forms.ToolStripMenuItem("ИИ (сеть, opt-in)", null, (_, _) => { Settings.AiOptIn = !Settings.AiOptIn; Settings.Save(); SyncMenu(); });
        menu.Items.Add(_pauseItem); menu.Items.Add(_ocrItem); menu.Items.Add(_actItem); menu.Items.Add(_aiItem);
        menu.Items.Add("Запомнить под курсором (через 3 с)", null, (_, _) => RememberUnderCursor(3));
        menu.Items.Add("⏹ Стоп всё (действие/серия)", null, (_, _) => Actions.Stop());
        menu.Items.Add("🛡 Строгий профиль (всё фоновое выкл.)", null, (_, _) => { Actions.Stop(); Learning = false; _overlay?.SafeClose(); Settings.Strict(); SyncMenu(); Watcher.Sync(); Rules.Sync(); Toast("Aura", "Строгий профиль: автоклик, действия, правила, ИИ, захват экрана и автозапуск выключены."); });
        menu.Items.Add("↩ Вернуть мой профиль", null, (_, _) => { _overlay?.SafeClose(); if (Settings.RestoreProfile()) { SyncMenu(); Watcher.Sync(); Rules.Sync(); Toast("Aura", "Прежние настройки восстановлены."); } else Toast("Aura", "Нечего возвращать — строгий профиль не включался."); });
        menu.Items.Add("▶ Последняя подсказка (Делаю 1 с + Стоп)", null, async (_, _) => await RunLastSuggestion());
        menu.Items.Add("↶ Забыть последнюю запомненную кнопку", null, (_, _) =>
        {
            var b = Buttons.List().OrderByDescending(x => x.Created).FirstOrDefault();
            if (b == null || (DateTime.Now - b.Created).TotalMinutes > 30) { Toast("Aura", "Нечего отменять (за последние 30 мин ничего не запоминал)."); return; }
            Buttons.Delete(b); _overlay?.Reload(); Toast("Aura", $"Забыл «{b.Name}» ({b.Process})."); Watcher.Sync();
        });
        menu.Items.Add("💤 Подсказки: отложить на 1 час", null, (_, _) =>
        {
            if (DateTime.Now < RulesSnoozeUntil) { RulesSnoozeUntil = DateTime.MinValue; Toast("Aura", "Подсказки снова включены."); }
            else { RulesSnoozeUntil = DateTime.Now.AddHours(1); Toast("Aura", $"Подсказки молчат до {RulesSnoozeUntil:HH:mm} (повтор — включить сразу)."); }
            RefreshLook();
        });
        var quiet = new Forms.ToolStripMenuItem("Подсказки тихо (звук, без всплывашек)") { CheckOnClick = true, Checked = Settings.RulesQuiet };
        quiet.CheckedChanged += (_, _) => { Settings.RulesQuiet = quiet.Checked; Settings.Save(); };
        menu.Items.Add(quiet);
        menu.Items.Add("Экспорт кнопок…", null, (_, _) =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { FileName = "aura-buttons.json", Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) try { Toast("Aura", $"Экспортировано: {Buttons.Export(d.FileName)} (без картинок)."); } catch (Exception ex) { Toast("Aura", "Ошибка экспорта: " + ex.Message); }
        });
        menu.Items.Add("Экспорт правил…", null, (_, _) =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { FileName = "aura-rules.json", Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) try { Toast("Aura", $"Правил экспортировано: {Rules.Export(d.FileName)}."); } catch (Exception ex) { Toast("Aura", "Ошибка: " + ex.Message); }
        });
        menu.Items.Add("Импорт правил…", null, (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) try { Toast("Aura", $"Новых правил: {Rules.Import(d.FileName)} — выключены (⏸), включи нужные в «💡 Правила»."); } catch (Exception ex) { Toast("Aura", "Ошибка: " + ex.Message); }
        });
        menu.Items.Add("Импорт кнопок…", null, (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) try { Toast("Aura", $"Импортировано новых: {Buttons.Import(d.FileName)} — выключены, включи нужные (вкл/выкл)."); _overlay?.Reload(); } catch (Exception ex) { Toast("Aura", "Ошибка импорта: " + ex.Message); }
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
    // v0.8.3: Esc cancels «Делаю» from anywhere — RegisterHotKey(Esc) ONLY while an action runs (no hooks), released right after
    System.Threading.CancellationTokenSource? _actionCts; HotkeyHost? _esc;
    public System.Threading.CancellationTokenSource? ActionCts
    {
        get => _actionCts;
        set { _actionCts = value; Dispatcher.BeginInvoke(new Action(SyncEsc)); }
    }
    void SyncEsc()
    {
        try
        {
            if (_actionCts != null && _esc == null) { _esc = new HotkeyHost(0xA0E, new[] { (0u, 0x1Bu, "Esc") }); _esc.Pressed += () => Actions.Stop(); }
            else if (_actionCts == null && _esc != null) { _esc.Dispose(); _esc = null; }
        }
        catch { }
    }
    static void CrashLog(object? ex)
    {
        try
        {
            var p = System.IO.Path.Combine(Aura.Settings.Dir, "crash.log"); System.IO.Directory.CreateDirectory(Aura.Settings.Dir);
            if (System.IO.File.Exists(p) && new System.IO.FileInfo(p).Length > 256 * 1024) System.IO.File.Delete(p); // cap 256 KB
            System.IO.File.AppendAllText(p, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Aura {Version}\n{ex}\n\n"); // text only — never screenshots
        }
        catch { }
    }
    public string LastApp = "—";
    /// «Учусь» — runtime only, visible (red dot). While ON, every Aura «Нажать» also remembers the button. No hooks.
    public bool Learning;
    /// Indicator stays visible while overlay is open, «Учусь» is ON, or «Действовать» is watching.
    /// v0.5.9: visible «ищу…» on the indicator while OCR / picture matching runs (so capture is never invisible).
    public string? Scanning;
    sealed class ScanScope : IDisposable { public void Dispose() { App.Current.Scanning = null; App.Current.RefreshLook(); } }
    public IDisposable Scan(string what) { Scanning = what; RefreshLook(); return new ScanScope(); }
    public void RefreshLook()
    {
        try
        {
            if (ActionCts != null) return; // «Делаю» owns the label
            Look.SetState(LastApp, Settings.Paused);
            if (_overlay != null || Learning || Watcher.On || Scanning != null) Look.Show(); else Look.Hide();
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
    public void TrayHint(string t)
    {
        if (_tray == null) return; var txt = "Aura · " + t; _tray.Text = txt.Length > 63 ? txt[..63] : txt;
        RestartHintFade();
    }
    // v0.8.0: one shared fade timer — an older hint's timer no longer wipes a newer hint early; also clears the 💡 in the indicator
    System.Windows.Threading.DispatcherTimer? _fade;
    void RestartHintFade()
    {
        if (_fade == null)
        {
            _fade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(31) };
            _fade.Tick += (_, _) => { _fade!.Stop(); if (_tray != null && _tray.Text.StartsWith("Aura · ")) _tray.Text = $"Aura — {_combo}"; RefreshLook(); };
        }
        _fade.Stop(); _fade.Start();
    }
    // v0.7.2: clicking a rule-hint balloon = the user's explicit OK → bring that window back and press via «Делаю» 1 с + Стоп (never without the click)
    DateTime _hintAt = DateTime.MinValue;
    DateTime _lastHintToast = DateTime.MinValue;
    public void ToastHint(string title, string text)
    {
        if ((DateTime.Now - _lastHintToast).TotalSeconds < 20) { TrayHint(text.Length > 40 ? text[..40] : text); return; } // v0.7.8: ≤1 hint balloon per 20 s, rest go to the tray tooltip
        Toast(title, text); _hintAt = DateTime.Now; _lastHintToast = DateTime.Now;
    }
    async void OnBalloonClicked()
    {
        var fresh = (DateTime.Now - _hintAt).TotalSeconds < 90; _hintAt = DateTime.MinValue;
        if (fresh) await RunLastSuggestion();
    }
    DateTime _suggAt = DateTime.MinValue;
    public DateTime RulesSnoozeUntil = DateTime.MinValue; // v0.8.2: in-memory only; restart clears it
    public void NoteSuggestion() { _suggAt = DateTime.Now; RefreshLook(); RestartHintFade(); }
    public DateTime SuggAt => _suggAt;
    // v0.7.7: also from tray «▶ Последняя подсказка» (explicit click, within 5 min)
    public async System.Threading.Tasks.Task RunLastSuggestion()
    {
        if ((DateTime.Now - _suggAt).TotalMinutes > 5) { Toast("Aura", "Свежих подсказок нет."); return; }
        if (LastSuggestion is not { } ls || Settings.Paused || ActionCts != null) return;
        var (r, fg) = ls;
        if (!Native.IsWindow(fg.Hwnd) || Settings.IsExcluded(fg.Process) || Settings.ActDisabled(fg.Process)) { Toast("Aura", "Окно закрыто или в «не трогать» — не нажимаю."); return; }
        Native.SetForegroundWindow(fg.Hwnd); await System.Threading.Tasks.Task.Delay(250);
        if (Settings.Paused || ActionCts != null || !Native.IsWindow(fg.Hwnd)) return; // v0.7.9: Pause during the hand-off cancels
        var lb = Buttons.ByName(r.Suggest, fg.Process);
        var kind = lb?.Action == ClickAction.DoubleClick ? "DOUBLECLICK" : lb?.Action == ClickAction.RightClick ? "RIGHTCLICK" : "CLICK";
        Actions.StopAll = false; r.Used++; try { Rules.Save(); } catch { }
        Toast("Aura", await Actions.Run(new Ai.Intent(kind, r.Suggest), fg, guardLeft: true));
    }
    // v0.7.8: any newer balloon un-arms the hint click (clicking an unrelated toast never presses); identical toasts within 5 s are dropped
    string _lastToast = ""; DateTime _lastToastAt = DateTime.MinValue;
    public void Toast(string title, string text)
    {
        var key = title + "|" + text;
        if (key == _lastToast && (DateTime.Now - _lastToastAt).TotalSeconds < 5) return;
        _lastToast = key; _lastToastAt = DateTime.Now; _hintAt = DateTime.MinValue;
        _tray?.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);
    }
    public static void OpenPath(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }
    /// v0.5.0 «Под курсором» (no hooks): read the control under the mouse ONCE, on hotkey/button press → remember it.
    public async void RememberUnderCursor(int delaySec)
    {
        if (Settings.Paused) { Toast("Aura", "Пауза — не учусь."); return; }
        if (delaySec > 0) { Toast("Aura · Под курсором", $"Наведи мышь на кнопку — запомню через {delaySec} с."); await System.Threading.Tasks.Task.Delay(delaySec * 1000); }
        if (Settings.Paused) { Toast("Aura", "Пауза — не учусь."); return; } // v0.6.1: paused during the 3 s wait
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
