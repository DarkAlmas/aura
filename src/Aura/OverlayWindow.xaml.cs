using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        OcrChk.IsChecked = App.Current.Settings.ScreenCaptureOptIn;
        AutoChk.IsChecked = App.Current.Settings.AutoClickOptIn;
        Status.Text = App.Current.Settings.ScreenCaptureOptIn ? "OCR: по запросу" : "OCR выкл.";
        if (Learn.Recording && fg != null && App.Current.Settings.IsExcluded(fg.Process)) { Learn.Stop(save: false); App.Current.Toast("Aura", "Учусь остановлен и сброшен: приложение в исключениях."); }
        SyncLearn(); LoadMacros();
        Loaded += (_, _) => { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth - 8; Top = wa.Bottom - ActualHeight - 8; };
        // Never Close() synchronously from activation events (WmActivate re-entrancy crash in v0.1.0).
        ContentRendered += (_, _) => _armed = true;
        Deactivated += (_, _) => { if (_armed && IsLoaded && !_acting) SafeClose(); };
        Closing += (_, _) => { _closing = true; ClearHighlight(); };
        Closed += (_, _) => { try { _hl?.Close(); } catch { } _hl = null; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_acting) App.Current.ActionCts?.Cancel(); else if (_sel != null) { Matches.SelectedItem = null; } else SafeClose(); } };
    }

    bool _armed, _closing, _acting;
    List<UiaFinder.Target>? _all;
    UiaFinder.Target? _sel;
    TargetHighlight? _hl;

    void ShowHighlight(UiaFinder.Target t)
    {
        try
        {
            Rect r = t.Bounds;
            if (t.Element != null) { try { var cur = t.Element.Current.BoundingRectangle; if (!cur.IsEmpty) r = cur; } catch { } }
            _hl ??= new TargetHighlight();
            _hl.ShowAt(r);
        }
        catch { /* highlight is cosmetic */ }
    }
    void ClearHighlight() { try { _hl?.Hide(); } catch { } }

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
            if (m.Count > 0) { Matches.SelectedIndex = 0; FindInfo.Text = $"Найдено: {m.Count} (из {_all.Count}). Выбери и нажми «Нажать»."; MaybeAuto(m.Count); }
            else
            {
                _sel = null; PressBtn.IsEnabled = false; ClearHighlight();
                if (!App.Current.Settings.ScreenCaptureOptIn)
                { FindInfo.Text = "UIA ничего не нашёл. Можно включить «OCR если UIA не нашёл» и искать снова."; return; }
                if (App.Current.Settings.Paused) { FindInfo.Text = "UIA ничего не нашёл. Aura на паузе — OCR не запускаю."; return; }
                FindInfo.Text = "UIA ничего не нашёл. Смотрю (OCR, один снимок окна)…";
                App.Current.Look.SetDoing("OCR"); App.Current.Look.Show();
                List<UiaFinder.Target>? o;
                try { o = await OcrFinder.Find(hwnd, q, OverlayPx()); }
                finally { App.Current.Look.SetState(_fg?.Process ?? "—", App.Current.Settings.Paused); }
                if (o == null) { FindInfo.Text = "OCR недоступен: в Windows нет языка распознавания (Параметры → Время и язык → Язык → добавить пакет с OCR)."; return; }
                Matches.ItemsSource = o;
                Matches.Visibility = o.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (o.Count > 0) { Matches.SelectedIndex = 0; FindInfo.Text = $"UIA пусто · OCR нашёл: {o.Count}. Выбери и нажми «Нажать»."; MaybeAuto(o.Count); }
                else FindInfo.Text = "Ни UIA, ни OCR не нашли такой текст.";
            }
        }
        catch (Exception ex) { FindInfo.Text = "UIA ошибка: " + ex.Message; }
    }

    /// Auto only right after a user-started Find, only for exactly one match, only via the same 1 s «Делаю»+Стоп path.
    void MaybeAuto(int count)
    {
        if (!App.Current.Settings.AutoClickOptIn || _sel == null || _acting) return;
        if (count != 1) { FindInfo.Text += " Авто: несколько совпадений — выбери сам."; return; }
        Press_Click(this, new RoutedEventArgs());
    }

    void AutoChk_Click(object s, RoutedEventArgs e)
    {
        App.Current.Settings.AutoClickOptIn = AutoChk.IsChecked == true; App.Current.Settings.Save();
        FindInfo.Text = App.Current.Settings.AutoClickOptIn
            ? "Авто вкл.: при одном совпадении после «Найти» сразу «Делаю» 1 с (Стоп/Esc/Пауза)."
            : "Авто выкл.: каждый клик — только «Нажать».";
    }

    void OcrChk_Click(object s, RoutedEventArgs e)
    {
        App.Current.Settings.ScreenCaptureOptIn = OcrChk.IsChecked == true; App.Current.Settings.Save();
        App.Current.SyncOcrMenu();
        Status.Text = App.Current.Settings.ScreenCaptureOptIn ? "OCR: по запросу" : "OCR выкл.";
    }

    /// Overlay bounds in physical px, so OCR never returns Aura's own text.
    Rect OverlayPx()
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return Rect.Empty;
            var m = src.CompositionTarget.TransformToDevice;
            return new Rect(Left * m.M11, Top * m.M22, ActualWidth * m.M11, ActualHeight * m.M22);
        }
        catch { return Rect.Empty; }
    }

    void Matches_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _sel = Matches.SelectedItem as UiaFinder.Target;
        PressBtn.IsEnabled = _sel != null;
        if (_sel != null)
        {
            ShowHighlight(_sel);
            FindInfo.Text = $"Цель подсвечена на экране: «{_sel.Label}» ({_sel.Type}). Esc — снять.";
        }
        else { ClearHighlight(); if (!_acting) FindInfo.Text = "Подсветка снята."; }
    }

    async void Press_Click(object s, RoutedEventArgs e)
    {
        if (_sel == null || _fg == null || _acting) return;
        if (App.Current.Settings.Paused) { FindInfo.Text = "Aura на паузе."; return; }
        if (App.Current.Settings.IsExcluded(_fg.Process)) { FindInfo.Text = "Приложение в исключениях — не нажимаю."; return; }
        var t = _sel;
        _acting = true;
        var cts = new CancellationTokenSource(); App.Current.ActionCts = cts;
        PressBtn.IsEnabled = false; StopBtn.Visibility = Visibility.Visible;
        App.Current.Look.SetDoing(t.Label); App.Current.Look.Show();
        FindInfo.Text = $"Делаю{(ReferenceEquals(s, this) ? " (авто)" : "")}: нажимаю «{t.Label}» через 1 с… (Стоп / Esc / Пауза)";
        try
        {
            await Task.Delay(1000, cts.Token);
            ClearHighlight();
            Native.SetForegroundWindow(_fg.Hwnd);
            await Task.Delay(150, cts.Token);
            var how = await Task.Run(() => UiaFinder.Click(t), cts.Token);
            if (Learn.Recording) Learn.Add(_fg.Process, t);
            App.Current.Toast("Aura", $"Нажал «{t.Label}» ({how}).");
            _acting = false; SafeClose();
        }
        catch (OperationCanceledException) { FindInfo.Text = "Остановлено."; Matches.SelectedItem = null; ClearHighlight(); }
        catch (Exception ex) { FindInfo.Text = "Не вышло: " + ex.Message; }
        finally
        {
            _acting = false; StopBtn.Visibility = Visibility.Collapsed; PressBtn.IsEnabled = _sel != null;
            App.Current.Look.SetState(_fg?.Process ?? "—", App.Current.Settings.Paused);
            if (ReferenceEquals(App.Current.ActionCts, cts)) App.Current.ActionCts = null;
        }
    }

    void Stop_Click(object s, RoutedEventArgs e) => App.Current.ActionCts?.Cancel();

    // ---- v0.3.0 «Учусь» ----
    void SyncLearn()
    {
        bool on = Learn.Recording;
        LearnBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LearnBadge.Text = on ? $"● ЗАПИСЬ · {Learn.Current!.Steps.Count} шагов" : "";
        LearnBtnText.Text = on ? "■ Стоп и сохранить" : "● Начать запись";
        LearnCancelBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }
    void LoadMacros()
    {
        var l = Learn.List();
        MacroList.ItemsSource = l;
        MacroList.Visibility = MacroBar.Visibility = l.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RepeatBtn.IsEnabled = false; MacroAllowChk.IsChecked = false;
    }
    void Learn_Click(object s, RoutedEventArgs e)
    {
        if (!Learn.Recording)
        {
            if (_fg != null && App.Current.Settings.IsExcluded(_fg.Process)) { LearnInfo.Text = "Приложение в исключениях — запись не начинаю."; return; }
            Learn.Start(); LearnInfo.Text = "Учусь: найди и «Нажать» нужные кнопки по очереди (Win+Shift+Z между шагами). Потом «Стоп и сохранить».";
        }
        else
        {
            var m = Learn.Stop();
            LearnInfo.Text = m == null ? "Запись пустая — ничего не сохранил." : $"Сохранил «{m.Name}» ({m.Steps.Count} шагов). Выбери, разреши и «Повторить».";
            LoadMacros();
        }
        SyncLearn();
    }
    void LearnCancel_Click(object s, RoutedEventArgs e) { Learn.Stop(save: false); LearnInfo.Text = "Запись отменена."; SyncLearn(); }
    void MacroList_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var m = MacroList.SelectedItem as Macro;
        RepeatBtn.IsEnabled = m != null && m.Allowed && !_acting;
        MacroAllowChk.IsChecked = m?.Allowed == true;
        if (m != null) LearnInfo.Text = string.Join(" → ", m.Steps.Select(x => $"{x.Process}: «{x.Label}»")) + (m.Allowed ? "" : "  (сначала «разрешить этот макрос»)");
    }
    void MacroAllow_Click(object s, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m) return;
        m.Allowed = MacroAllowChk.IsChecked == true; Learn.Save(m);
        RepeatBtn.IsEnabled = m.Allowed && !_acting;
    }
    void MacroDelete_Click(object s, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m) return;
        Learn.Delete(m); LoadMacros(); LearnInfo.Text = $"Удалил «{m.Name}».";
    }
    async void Repeat_Click(object s, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not Macro m || _acting) return;
        if (!m.Allowed) { LearnInfo.Text = "Сначала «разрешить этот макрос»."; return; }
        if (Learn.Recording) { LearnInfo.Text = "Сначала останови запись."; return; }
        _acting = true;
        var cts = new CancellationTokenSource(); App.Current.ActionCts = cts;
        RepeatBtn.IsEnabled = false; StopBtn.Visibility = Visibility.Visible;
        int done = 0;
        try
        {
            for (int i = 0; i < m.Steps.Count; i++)
            {
                var st = m.Steps[i];
                if (App.Current.Settings.Paused) throw new OperationCanceledException();
                if (App.Current.Settings.IsExcluded(st.Process)) { LearnInfo.Text = $"Шаг {i + 1}: {st.Process} в исключениях — остановил."; return; }
                var (hwnd, t) = await Task.Run(() => Learn.Resolve(st), cts.Token);
                if (t == null) { LearnInfo.Text = $"Шаг {i + 1}: не нашёл «{st.Label}» в {st.Process} — остановил."; return; }
                ShowHighlight(t);
                App.Current.Look.SetDoing($"{i + 1}/{m.Steps.Count} {t.Label}"); App.Current.Look.Show();
                LearnInfo.Text = $"Делаю шаг {i + 1}/{m.Steps.Count}: «{t.Label}» через 1 с… (Стоп / Esc / Пауза)";
                await Task.Delay(1000, cts.Token);
                if (App.Current.Settings.Paused) throw new OperationCanceledException();
                ClearHighlight();
                Native.SetForegroundWindow(hwnd);
                await Task.Delay(300, cts.Token);
                await Task.Run(() => UiaFinder.Click(t), cts.Token);
                done++;
                await Task.Delay(400, cts.Token);
            }
            App.Current.Toast("Aura", $"Макрос «{m.Name}»: {done} шагов выполнено.");
            LearnInfo.Text = $"Готово: {done} шагов.";
        }
        catch (OperationCanceledException) { LearnInfo.Text = $"Остановлено после {done} шагов."; ClearHighlight(); }
        catch (Exception ex) { LearnInfo.Text = "Не вышло: " + ex.Message; ClearHighlight(); }
        finally
        {
            _acting = false; StopBtn.Visibility = Visibility.Collapsed; RepeatBtn.IsEnabled = (MacroList.SelectedItem as Macro)?.Allowed == true;
            App.Current.Look.SetState(_fg?.Process ?? "—", App.Current.Settings.Paused);
            if (ReferenceEquals(App.Current.ActionCts, cts)) App.Current.ActionCts = null;
        }
    }

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
