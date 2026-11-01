using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Aura;

public partial class OverlayWindow : Window
{
    readonly Native.WindowInfo? _fg;

    public OverlayWindow(Native.WindowInfo? fg)
    {
        InitializeComponent();
        HotkeyHint.Text = App.Current.HotkeyName;
        _fg = fg;
        Ctx.Text = "СЕЙЧАС · " + Kind(fg?.Process).ToUpperInvariant();
        NextText.Text = Suggest(fg?.Process);
        Status.Text = App.Current.Settings.ScreenCaptureOptIn ? "OCR: opt-in (заглушка)" : "OCR выкл.";
        Loaded += (_, _) => { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth - 8; Top = wa.Bottom - ActualHeight - 8; };
        // Never Close() synchronously from activation events (WmActivate re-entrancy crash in v0.1.0).
        ContentRendered += (_, _) => _armed = true;
        Deactivated += (_, _) => { if (_armed && IsLoaded && !_acting) SafeClose(); };
        Closing += (_, _) => _closing = true;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_acting) App.Current.ActionCts?.Cancel(); else SafeClose(); } };
    }

    bool _armed, _closing, _acting;
    List<UiaFinder.Target>? _all;
    UiaFinder.Target? _sel;

    void FindBox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { Find_Click(s, e); e.Handled = true; } }

    async void Find_Click(object s, RoutedEventArgs e)
    {
        if (_fg == null || !Native.IsWindow(_fg.Hwnd)) { FindInfo.Text = "Нет активного окна."; return; }
        if (App.Current.Settings.IsExcluded(_fg.Process)) { FindInfo.Text = "Приложение в исключениях — не читаю."; return; }
        FindInfo.Text = "Ищу…";
        var hwnd = _fg.Hwnd; var q = FindBox.Text;
        try
        {
            _all ??= await Task.Run(() => UiaFinder.Enumerate(hwnd));
            var m = UiaFinder.Match(_all, q);
            Matches.ItemsSource = m;
            Matches.Visibility = m.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (m.Count > 0) { Matches.SelectedIndex = 0; FindInfo.Text = $"Найдено: {m.Count} (из {_all.Count}). Выбери и нажми «Нажать»."; }
            else
            {
                _sel = null; PressBtn.IsEnabled = false;
                FindInfo.Text = App.Current.Settings.ScreenCaptureOptIn
                    ? "UIA ничего не нашёл. OCR пока заглушка (будет в следующей версии)."
                    : "UIA ничего не нашёл. Для поиска по экрану включи OCR в трее (пока заглушка).";
            }
        }
        catch (Exception ex) { FindInfo.Text = "UIA ошибка: " + ex.Message; }
    }

    void Matches_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _sel = Matches.SelectedItem as UiaFinder.Target;
        PressBtn.IsEnabled = _sel != null;
        if (_sel != null) FindInfo.Text = $"Цель: «{_sel.Label}» ({_sel.Type}) · x{(int)_sel.Bounds.X} y{(int)_sel.Bounds.Y} {(int)_sel.Bounds.Width}×{(int)_sel.Bounds.Height}";
    }

    async void Press_Click(object s, RoutedEventArgs e)
    {
        if (_sel == null || _fg == null || _acting) return;
        if (App.Current.Settings.Paused) { FindInfo.Text = "Aura на паузе."; return; }
        var t = _sel;
        _acting = true;
        var cts = new CancellationTokenSource(); App.Current.ActionCts = cts;
        PressBtn.IsEnabled = false; StopBtn.Visibility = Visibility.Visible;
        App.Current.Look.SetDoing(t.Label); App.Current.Look.Show();
        FindInfo.Text = $"Делаю: нажимаю «{t.Label}» через 1 с… (Стоп / Esc / Пауза)";
        try
        {
            await Task.Delay(1000, cts.Token);
            Native.SetForegroundWindow(_fg.Hwnd);
            await Task.Delay(150, cts.Token);
            var how = await Task.Run(() => UiaFinder.Click(t), cts.Token);
            App.Current.Toast("Aura", $"Нажал «{t.Label}» ({how}).");
            _acting = false; SafeClose();
        }
        catch (OperationCanceledException) { FindInfo.Text = "Остановлено."; }
        catch (Exception ex) { FindInfo.Text = "Не вышло: " + ex.Message; }
        finally
        {
            _acting = false; StopBtn.Visibility = Visibility.Collapsed; PressBtn.IsEnabled = _sel != null;
            App.Current.Look.SetState(_fg?.Process ?? "—", App.Current.Settings.Paused);
            if (ReferenceEquals(App.Current.ActionCts, cts)) App.Current.ActionCts = null;
        }
    }

    void Stop_Click(object s, RoutedEventArgs e) => App.Current.ActionCts?.Cancel();

    // Deferred, idempotent close: safe from any event handler.
    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { try { Close(); } catch (InvalidOperationException) { } }));
    }

    static string Kind(string? p) => (p ?? "").ToLowerInvariant() switch
    {
        "msedge" or "chrome" or "firefox" or "opera" or "browser" or "yandex" => "браузер",
        "winword" or "excel" or "powerpnt" or "onenote" => "office",
        "explorer" => "проводник",
        "code" or "devenv" or "cursor" => "код",
        "" => "нет окна",
        _ => p!
    };
    static string Suggest(string? p) => Kind(p) switch
    {
        "браузер" => "Сохрани нужное в заметку или поставь таймер фокуса на 25 мин.",
        "office" => "Ctrl+S — сохранить; потом заметка, что осталось доделать.",
        "проводник" => "Запиши путь папки в заметку, чтобы вернуться.",
        "код" => "Закоммить прогресс и запиши следующий шаг в заметку.",
        _ => "Сверни лишнее или запиши мысль в заметку."
    };

    void Screen_Click(object s, RoutedEventArgs e) =>
        ScreenText.Text = _fg == null ? "Нет активного окна." : $"«{_fg.Title}»\nПроцесс: {_fg.Process}.exe (pid {_fg.Pid})";

    void Next_Click(object s, RoutedEventArgs e) => Note_Click(s, e);

    void Minimize_Click(object s, RoutedEventArgs e)
    {
        if (_fg != null && Native.IsWindow(_fg.Hwnd)) Native.ShowWindow(_fg.Hwnd, Native.SW_MINIMIZE);
        SafeClose();
    }

    void Note_Click(object s, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aura");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "notes.txt");
        File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {_fg?.Process ?? "-"} — {_fg?.Title ?? "-"}{Environment.NewLine}");
        System.Diagnostics.Process.Start("notepad.exe", $"\"{file}\"");
        SafeClose();
    }

    void Timer_Click(object s, RoutedEventArgs e)
    {
        int min = int.Parse((string)((FrameworkElement)s).Tag);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(min) };
        t.Tick += (_, _) => { t.Stop(); App.Current.Toast("Aura · таймер", $"{min} мин прошло."); };
        t.Start();
        App.Current.Toast("Aura · таймер", $"Запущен на {min} мин.");
        SafeClose();
    }

    void Close_Click(object s, RoutedEventArgs e) => SafeClose();
}
