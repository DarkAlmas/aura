using System;
using System.Collections.Generic;
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
        Ctx.Text = "AURA · " + (fg?.Process ?? "нет окна").ToUpperInvariant();
        OcrChk.IsChecked = App.Current.Settings.ScreenCaptureOptIn;
        ActChk.IsChecked = App.Current.Settings.ActOptIn;
        Status.Text = App.Current.Settings.ScreenCaptureOptIn ? "OCR: по запросу" : "OCR выкл.";
        if (App.Current.Learning && fg != null && App.Current.Settings.IsExcluded(fg.Process)) { App.Current.Learning = false; App.Current.Toast("Aura", "Учусь выключен: приложение в исключениях."); }
        SyncLearn(); LoadButtons();
        Loaded += (_, _) => { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth - 8; Top = wa.Bottom - ActualHeight - 8; };
        // Never Close() synchronously from activation events (WmActivate re-entrancy crash in v0.1.0).
        ContentRendered += (_, _) => _armed = true;
        Deactivated += (_, _) => { if (_armed && IsLoaded && !_acting) SafeClose(); };
        Closing += (_, _) => { _closing = true; ClearHighlight(); };
        Closed += (_, _) => { try { _hl?.Close(); } catch { } _hl = null; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_acting || App.Current.ActionCts != null) App.Current.ActionCts?.Cancel(); else if (_sel != null) { Matches.SelectedItem = null; } else SafeClose(); } };
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
            if (m.Count > 0) { Matches.SelectedIndex = 0; FindInfo.Text = $"Найдено: {m.Count} (из {_all.Count}). Выбери и нажми «Нажать»."; }
            else
            {
                _sel = null; PressBtn.IsEnabled = RememberBtn.IsEnabled = false; ClearHighlight();
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
                if (o.Count > 0) { Matches.SelectedIndex = 0; FindInfo.Text = $"UIA пусто · OCR нашёл: {o.Count}. Выбери и нажми «Нажать»."; }
                else FindInfo.Text = "Ни UIA, ни OCR не нашли такой текст.";
            }
        }
        catch (Exception ex) { FindInfo.Text = "UIA ошибка: " + ex.Message; }
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
        PressBtn.IsEnabled = _sel != null; RememberBtn.IsEnabled = _sel != null && !string.IsNullOrWhiteSpace(_sel.Name);
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
        FindInfo.Text = $"Делаю: нажимаю «{t.Label}» через 1 с… (Стоп / Esc / Пауза)";
        try
        {
            await Task.Delay(1000, cts.Token);
            ClearHighlight();
            Native.SetForegroundWindow(_fg.Hwnd);
            await Task.Delay(150, cts.Token);
            var how = await Task.Run(() => UiaFinder.Click(t), cts.Token);
            var learned = App.Current.Learning ? Buttons.Remember(_fg.Process, t) : null;
            App.Current.Toast("Aura", $"Нажал «{t.Label}» ({how})." + (learned != null ? $" Запомнил: {learned.Label}" : ""));
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

    // ---- v0.4.0 выученные кнопки (только через UI Aura, без хуков) ----
    void SyncLearn()
    {
        bool on = App.Current.Learning;
        LearnBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LearnBtnText.Text = on ? "■ Хватит учиться" : "● Учусь";
    }
    void LoadButtons()
    {
        var l = Buttons.List().ToList();
        BtnList.ItemsSource = l;
        BtnList.Visibility = BtnBar.Visibility = l.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    void Learn_Click(object s, RoutedEventArgs e)
    {
        if (!App.Current.Learning && _fg != null && App.Current.Settings.IsExcluded(_fg.Process)) { LearnInfo.Text = "Приложение в исключениях — не учусь."; return; }
        App.Current.Learning = !App.Current.Learning;
        LearnInfo.Text = App.Current.Learning ? "Учусь: каждое «Нажать» через Aura запоминает кнопку." : "Учёба выключена.";
        SyncLearn(); App.Current.RefreshLook();
    }
    void Remember_Click(object s, RoutedEventArgs e)
    {
        if (_sel == null || _fg == null) return;
        var b = Buttons.Remember(_fg.Process, _sel);
        LearnInfo.Text = b == null ? "Не запомнил: пустое имя или приложение в исключениях." : $"Запомнил: {b.Label} ({b.Process}).";
        if (b != null) App.Current.Toast("Aura", $"Запомнил: {b.Label}");
        LoadButtons();
    }
    void ActChk_Changed(object s, RoutedEventArgs e)
    {
        App.Current.Settings.ActOptIn = ActChk.IsChecked == true; App.Current.Settings.Save();
        Watcher.Sync();
        LearnInfo.Text = App.Current.Settings.ActOptIn
            ? "Действую: в активном окне ищу выученные кнопки каждые 2 с → «Делаю» 1 с (Стоп/Пауза) → клик."
            : "Не действую: сам ничего не жму.";
    }
    void BtnDelete_Click(object s, RoutedEventArgs e)
    {
        if (BtnList.SelectedItem is not LearnedButton b) { LearnInfo.Text = "Выбери кнопку в списке."; return; }
        Buttons.Delete(b); LoadButtons(); LearnInfo.Text = $"Забыл: {b.Label}.";
    }
    void BtnClear_Click(object s, RoutedEventArgs e) { Buttons.Clear(); LoadButtons(); LearnInfo.Text = "Список очищен."; }

    // Deferred, idempotent close: safe from any event handler.
    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { try { Close(); } catch (InvalidOperationException) { } }));
    }

    void Close_Click(object s, RoutedEventArgs e) => SafeClose();
}
