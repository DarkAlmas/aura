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
        InitializeComponent(); Title = $"Aura {App.Version}";
        HotkeyHint.Text = App.Current.HotkeyName;
        _fg = fg;
        Ctx.Text = "AURA · " + (fg?.Process ?? "нет окна").ToUpperInvariant();
        OcrChk.IsChecked = App.Current.Settings.ScreenCaptureOptIn;
        ActChk.IsChecked = App.Current.Settings.ActOptIn;
        Status.Text = App.Current.Settings.ScreenCaptureOptIn ? "OCR: по запросу" : "OCR выкл.";
        if (App.Current.Learning && fg != null && App.Current.Settings.IsExcluded(fg.Process)) { App.Current.Learning = false; App.Current.Toast("Aura", "Учусь выключен: приложение в исключениях."); }
        SyncLearn(); LoadButtons();
        var st = App.Current.Settings;
        ActCombo.SelectedIndex = (int)st.DefaultAction; TplChk.IsChecked = st.SaveTemplates;
        AiChk.IsChecked = st.AiOptIn; AiTextChk.IsChecked = st.AiAttachText;
        _aiInit = true; AiBackend.SelectedIndex = st.AiBaseUrl == Ai.ProxyBase ? 0 : st.AiBaseUrl == Ai.OllamaBase ? 1 : 2; _aiInit = false;
        StartChk.IsChecked = st.StartupOptIn; LoadStartup();
        AppOffChk.IsChecked = fg != null && st.ActDisabled(fg.Process); AppOffChk.IsEnabled = fg != null;
        if (fg != null) AppOffChk.Content = $"не действовать в {fg.Process}";
        DelayBox.Text = st.StepDelayMs.ToString(); SoundChk.IsChecked = st.ClickSound;
        if (Ai.History.Count > 0) AiReply.Text = HistText();
        LoadNt(); if (fg != null) NtCurText.Text = "← " + fg.Process;
        RulesChk.IsChecked = st.RulesOptIn; LoadRules(); _ = CheckRules();
        // v0.6.6: Checked/Unchecked (not Click) so UIA TogglePattern (Formal / screen readers) runs the same handlers as a mouse click. Subscribed after initial IsChecked loads.
        OcrChk.Checked += OcrChk_Click; OcrChk.Unchecked += OcrChk_Click;
        TplChk.Checked += TplChk_Click; TplChk.Unchecked += TplChk_Click;
        OnlyAppChk.Checked += OnlyAppChk_Click; OnlyAppChk.Unchecked += OnlyAppChk_Click;
        AppOffChk.Checked += AppOffChk_Click; AppOffChk.Unchecked += AppOffChk_Click;
        AiChk.Checked += AiChk_Click; AiChk.Unchecked += AiChk_Click;
        AiTextChk.Checked += AiChk_Click; AiTextChk.Unchecked += AiChk_Click;
        StartChk.Checked += StartChk_Click; StartChk.Unchecked += StartChk_Click;
        SoundChk.Checked += SoundChk_Click; SoundChk.Unchecked += SoundChk_Click;
        RulesChk.Checked += RulesChk_Click; RulesChk.Unchecked += RulesChk_Click;
        UnderText.Text = "🖱 Под курсором (" + App.Current.UnderCursorHotkey + ")";
        Status.Text = "v" + App.Version + " · " + Status.Text;
        Loaded += (_, _) => { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth - 8; Top = wa.Bottom - ActualHeight - 8; };
        // Never Close() synchronously from activation events (WmActivate re-entrancy crash in v0.1.0).
        ContentRendered += (_, _) => _armed = true;
        Deactivated += (_, _) => { if (_armed && IsLoaded && !_acting) SafeClose(); };
        Closing += (_, _) => { _closing = true; ClearHighlight(); DelayBox_LostFocus(this, new RoutedEventArgs()); };
        Closed += (_, _) => { try { _hl?.Close(); } catch { } _hl = null; };
        KeyDown += (_, e) => { if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase) { BtnUndo_Click(this, new RoutedEventArgs()); e.Handled = true; } }; // v0.9.8 Ctrl+Z = forget last remembered
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_acting || App.Current.ActionCts != null) App.Current.ActionCts?.Cancel(); else if (_sel != null) { Matches.SelectedItem = null; } else SafeClose(); } };
    }

    bool _armed, _closing, _acting;
    List<UiaFinder.Target>? _all; DateTime _allAt;
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
            if (_all == null || DateTime.Now - _allAt > TimeSpan.FromSeconds(5)) { _all = await Task.Run(() => UiaFinder.Enumerate(hwnd)); _allAt = DateTime.Now; } // v0.6.1: no stale UIA snapshot
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
        if (App.Current.ActionCts != null) { FindInfo.Text = "Уже выполняется другое действие — дождись или Стоп."; return; } // v0.6.1
        var t = _sel;
        _acting = true; Actions.StopAll = false; // v0.9.3: an old Стоп/Esc must not silently cancel a NEW explicit press
        var cts = new CancellationTokenSource(); App.Current.ActionCts = cts;
        PressBtn.IsEnabled = false; StopBtn.Visibility = Visibility.Visible;
        App.Current.Look.SetDoing(t.Label); App.Current.Look.Show();
        FindInfo.Text = $"Делаю: нажимаю «{t.Label}» через 1 с… (Стоп / Esc / Пауза)";
        try
        {
            await Task.Delay(1000, cts.Token);
            if (App.Current.Settings.Paused || Actions.StopAll) throw new OperationCanceledException(); // v0.6.1: Пауза during «Делаю»
            if (!Native.IsWindow(_fg.Hwnd)) { FindInfo.Text = "Окно закрылось — не нажимаю."; return; }
            ClearHighlight();
            Native.SetForegroundWindow(_fg.Hwnd);
            await Task.Delay(150, cts.Token);
            var act = CurAct;
            var how = await Task.Run(() => UiaFinder.Click(t, act), cts.Token);
            if (App.Current.Settings.ClickSound) System.Media.SystemSounds.Asterisk.Play();
            var learned = App.Current.Learning ? Buttons.Remember(_fg.Process, t, act, App.Current.Settings.SaveTemplates) : null;
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
    void OnlyAppChk_Click(object s, RoutedEventArgs e) => LoadButtons();
    void LoadButtons()
    {
        // v0.6.0 profiles: current app's buttons first; «только это приложение» filter.
        var all = Buttons.List();
        bool Mine(LearnedButton b) => _fg != null && string.Equals(b.Process, _fg.Process, StringComparison.OrdinalIgnoreCase);
        var l = (OnlyAppChk?.IsChecked == true && _fg != null ? all.Where(Mine) : all).OrderByDescending(Mine).ThenBy(b => b.Process).ThenBy(b => b.Name).ToList();
        if (OnlyAppChk != null) OnlyAppChk.Content = _fg == null ? "только это приложение" : $"только {_fg.Process} ({all.Count(Mine)} из {all.Count})";
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
        var b = Buttons.Remember(_fg.Process, _sel, CurAct, App.Current.Settings.SaveTemplates);
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
    void BtnUndo_Click(object s, RoutedEventArgs e)
    {
        var b = Buttons.List().OrderByDescending(x => x.Created).FirstOrDefault();
        if (b == null || (DateTime.Now - b.Created).TotalMinutes > 30) { LearnInfo.Text = "Нечего отменять (за 30 мин ничего не запоминал)."; return; }
        Buttons.Delete(b); LoadButtons(); LearnInfo.Text = $"Отменил: {b.Label}."; Watcher.Sync();
    }
    void BtnClear_Click(object s, RoutedEventArgs e) { var n = Buttons.List().Count; if (n == 0) return; if (MessageBox.Show(this, $"Забыть все {n} кнопок? Это не отменить.", "Aura", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return; Buttons.Clear(); LoadButtons(); LearnInfo.Text = "Список очищен."; }

    // ---- v0.5.0 ----
    bool _aiInit;
    List<Ai.Intent>? _intents;
    ClickAction CurAct => (ClickAction)Math.Max(0, ActCombo.SelectedIndex);
    public void Reload() { try { LoadButtons(); } catch { } }
    void ActCombo_Changed(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Current.Settings.DefaultAction = CurAct; App.Current.Settings.Save();
        if (BtnList?.SelectedItem is LearnedButton b) { b.Action = CurAct; Buttons.Update(); LoadButtons(); LearnInfo.Text = $"«{b.Label}»: действие → {ActCombo.Text}."; }
    }
    void TplChk_Click(object s, RoutedEventArgs e) { App.Current.Settings.SaveTemplates = TplChk.IsChecked == true; App.Current.Settings.Save(); }
    void UnderCursor_Click(object s, RoutedEventArgs e) { SafeClose(); App.Current.RememberUnderCursor(3); }

    async void BtnRun_Click(object s, RoutedEventArgs e)
    {
        if (BtnList.SelectedItem is not LearnedButton b) { LearnInfo.Text = "Выбери кнопку в списке."; return; }
        if (_fg == null) { LearnInfo.Text = "Нет активного окна."; return; }
        _acting = true; LearnInfo.Text = $"Делаю: «{b.Label}» через 1 с… (Стоп в индикаторе / Esc)";
        try { LearnInfo.Text = await Actions.Run(new Ai.Intent(b.Action == ClickAction.DoubleClick ? "DOUBLECLICK" : b.Action == ClickAction.RightClick ? "RIGHTCLICK" : "CLICK", b.Name), _fg); }
        finally { _acting = false; }
    }
    async void BtnSeries_Click(object s, RoutedEventArgs e)
    {
        if (_fg == null) return;
        var l = Buttons.List().Where(b => string.Equals(b.Process, _fg.Process, StringComparison.OrdinalIgnoreCase)).ToList();
        if (l.Count == 0) { LearnInfo.Text = $"Для {_fg.Process} нет выученных кнопок."; return; }
        _acting = true; LearnInfo.Text = $"Серия: {l.Count} шт., каждая с «Делаю» 1 с.";
        try { var m = await Actions.RunSeries(l, _fg); if (!_closing) LearnInfo.Text = m; } finally { _acting = false; }
    }
    void BtnStartup_Click(object s, RoutedEventArgs e)
    {
        if (BtnList.SelectedItem is not LearnedButton b) { LearnInfo.Text = "Выбери кнопку в списке."; return; }
        App.Current.Settings.StartupActions.Add(new StartupAction { Kind = "RunButton", Arg = b.Name }); App.Current.Settings.Save(); LoadStartup();
        LearnInfo.Text = $"«{b.Label}» добавлена в «При старте»."; StartExp.IsExpanded = true;
    }

    void AiChk_Click(object s, RoutedEventArgs e)
    {
        var st = App.Current.Settings; st.AiOptIn = AiChk.IsChecked == true; st.AiAttachText = AiTextChk.IsChecked == true; st.Save(); App.Current.SyncOcrMenu();
    }
    void AiBackend_Changed(object s, RoutedEventArgs e)
    {
        if (_aiInit) return;
        var st = App.Current.Settings;
        if (AiBackend.SelectedIndex == 0) { st.AiBaseUrl = Ai.ProxyBase; st.AiModel = "openai/gpt-oss-20b"; }
        else if (AiBackend.SelectedIndex == 1) { st.AiBaseUrl = Ai.OllamaBase; if (st.AiModel.Contains('/')) st.AiModel = "qwen2.5:7b"; }
        else { AiReply.Text = "Свой URL/модель — в settings.json (AiBaseUrl, AiModel). Ключ: впиши \"AiKey\" — при запуске Aura зашифрует его (DPAPI) и удалит открытый текст."; App.OpenPath(Settings.Dir); }
        st.Save();
    }
    void AiChip_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement f && f.Tag is string q) { AskBox.Text = q; Ask_Click(s, e); } } // v0.6.2 quick prompts (still need «ИИ (сеть)»)
    void AskBox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { Ask_Click(s, e); e.Handled = true; } }
    async void Ask_Click(object s, RoutedEventArgs e)
    {
        var q = AskBox.Text.Trim(); if (q.Length == 0) return;
        var st = App.Current.Settings;
        if (!st.AiOptIn) { AiReply.Text = "Включи «ИИ (сеть)» — без этого Aura в сеть не ходит."; return; }
        if (st.Paused) { AiReply.Text = "Пауза."; return; }
        AskBtn.IsEnabled = false; AiReply.Text = "Думаю…"; _acting = true;
        try
        {
            string? text = null;
            if (st.AiAttachText && st.ScreenCaptureOptIn && _fg != null && !st.IsExcluded(_fg.Process) && OcrFinder.Available)
            {
                var w = await OcrFinder.Find(_fg.Hwnd, "", OverlayPx());
                if (w != null) text = string.Join(" | ", w.Where(t => t.Name.Contains(' ') || t.Name.Length > 1).Select(t => t.Name).Distinct().Take(200));
            }
            var fg = _fg != null && st.IsExcluded(_fg.Process) ? null : _fg;
            int before = Ai.History.Count;
            var (reply, intents) = await Ai.Ask(q, fg, text);
            AiReply.Text = Ai.History.Count > before ? HistText() : (HistText() + "\n\n⚠ " + reply).Trim();
            AskBox.Text = ""; AiScroll.ScrollToEnd(); _intents = intents;
            IntentList.ItemsSource = intents; IntentList.Visibility = IntentBar.Visibility = intents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            IntentList.SelectAll();
        }
        finally { AskBtn.IsEnabled = true; _acting = false; }
    }
    async void RunIntents_Click(object s, RoutedEventArgs e)
    {
        var sel = IntentList.SelectedItems.Cast<Ai.Intent>().ToList(); if (sel.Count == 0) return;
        if (_fg == null && sel.Any(i => i.Kind is not ("LAUNCH" or "OPEN" or "WAIT"))) { AiReply.Text = "Нет активного окна — клики не выполняю."; return; }
        _acting = true; var log = new List<string>(); Actions.StopAll = false;
        try
        {
            foreach (var it in (_intents ?? new()).Where(sel.Contains))
            {
                var r = await Actions.Run(it, _fg, guardLeft: true, fromAi: true); log.Add(r); AiReply.Text = string.Join("\n", log);
                if (r.StartsWith("Остановлено") || App.Current.Settings.Paused || Actions.StopAll) { log.Add("Прервано — остальные пункты не выполнены."); AiReply.Text = string.Join("\n", log); break; }
                await Task.Delay(Math.Clamp(App.Current.Settings.StepDelayMs, 200, 10000));
            }
        }
        finally { _acting = false; LoadButtons(); }
    }

    static string HistText() => string.Join("\n\n", Ai.History.TakeLast(8).Select(h => (h.Role == "user" ? "Ты: " : "Aura: ") + h.Text.Trim()));
    void AiReset_Click(object s, RoutedEventArgs e) { Ai.Reset(); AiReply.Text = "Новый чат."; IntentList.ItemsSource = null; IntentList.Visibility = IntentBar.Visibility = Visibility.Collapsed; }
    void DelayBox_LostFocus(object s, RoutedEventArgs e)
    {
        if (int.TryParse(DelayBox.Text, out var v)) { App.Current.Settings.StepDelayMs = Math.Clamp(v, 200, 10000); App.Current.Settings.Save(); }
        DelayBox.Text = App.Current.Settings.StepDelayMs.ToString();
    }
    // ---- v0.5.8 «Не трогать»: ExcludedApps (🔒) + ActDisabledApps (✋) in one list
    sealed record NtItem(string Process, bool Excluded) { public override string ToString() => (Excluded ? "🔒 " : "✋ ") + Process; }
    void LoadNt()
    {
        var st = App.Current.Settings;
        NtList.ItemsSource = st.ExcludedApps.Select(p => new NtItem(p, true)).Concat(st.ActDisabledApps.Select(p => new NtItem(p, false))).OrderBy(i => i.Process, StringComparer.OrdinalIgnoreCase).ToList();
    }
    string NtValue() { var v = NtName.Text.Trim(); if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = v[..^4]; return v; }
    void NtCurrent_Click(object s, RoutedEventArgs e) { if (_fg != null) NtName.Text = _fg.Process; }
    void NtAdd(List<string> l)
    {
        var v = NtValue(); if (v.Length == 0) { NtName.Focus(); return; }
        if (!l.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) l.Add(v);
        App.Current.Settings.Save(); NtName.Text = ""; LoadNt(); SyncNtFlags();
    }
    void NtExclude_Click(object s, RoutedEventArgs e) => NtAdd(App.Current.Settings.ExcludedApps);
    void NtNoAct_Click(object s, RoutedEventArgs e) => NtAdd(App.Current.Settings.ActDisabledApps);
    void NtDel_Click(object s, RoutedEventArgs e)
    {
        if (NtList.SelectedItem is not NtItem i) return; var st = App.Current.Settings;
        (i.Excluded ? st.ExcludedApps : st.ActDisabledApps).RemoveAll(x => string.Equals(x, i.Process, StringComparison.OrdinalIgnoreCase));
        st.Save(); LoadNt(); SyncNtFlags();
    }
    void SyncNtFlags() { if (_fg != null) AppOffChk.IsChecked = App.Current.Settings.ActDisabled(_fg.Process); }

    // ---- v0.5.7 rules (suggest only)
    void LoadRules() { RuleList.ItemsSource = null; RuleList.ItemsSource = Rules.List().ToList(); }
    async Task CheckRules()
    {
        try
        {
            if (_fg == null || App.Current.Settings.IsExcluded(_fg.Process) || !Rules.For(_fg.Process).Any()) return;
            var uia = await Task.Run(() => UiaFinder.Enumerate(_fg.Hwnd));
            var hits = Rules.Hits(_fg.Process, uia);
            if (hits.Count == 0) return;
            RuleHits.ItemsSource = hits; RuleHits.SelectedIndex = 0;
            if (App.Current.LastSuggestion is { } ls && ls.Fg.Hwnd == _fg.Hwnd && hits.Contains(ls.Rule)) RuleHits.SelectedItem = ls.Rule; // v0.6.7: preselect the one from the toast
            RuleHits.Visibility = RuleHitRun.Visibility = Visibility.Visible; RuleExp.IsExpanded = true;
        }
        catch { }
    }
    async void RuleHitRun_Click(object s, RoutedEventArgs e)
    {
        if (RuleHits.SelectedItem is not Rule r || _fg == null || _acting) return;
        _acting = true; Actions.StopAll = false;
        try { var lb = Buttons.ByName(r.Suggest, _fg.Process); var kind = lb?.Action == ClickAction.DoubleClick ? "DOUBLECLICK" : lb?.Action == ClickAction.RightClick ? "RIGHTCLICK" : "CLICK";
              r.Used++; try { Rules.Save(); } catch { } LoadRules();
              LearnInfo.Text = await Actions.Run(new Ai.Intent(kind, r.Suggest), _fg, guardLeft: true); }
        finally { _acting = false; }
    }
    void RulesChk_Click(object s, RoutedEventArgs e) { App.Current.Settings.RulesOptIn = RulesChk.IsChecked == true; App.Current.Settings.Save(); Rules.Sync(); }
    // v0.6.8: presets only fill the boxes; the user still presses «Добавить» (and rules still only suggest)
    // v0.6.9: «Вижу» dropdown = visible UIA names of the current window (one read, in memory, on demand)
    async void RuleWhen_DropDown(object s, EventArgs e)
    {
        if (_fg == null || RuleWhen.Items.Count > 0 || App.Current.Settings.IsExcluded(_fg.Process)) return;
        try
        {
            var uia = await Task.Run(() => UiaFinder.Enumerate(_fg.Hwnd));
            foreach (var n in uia.Select(t => (t.Name ?? "").Trim()).Where(n => n.Length >= 2 && n.Length <= 60).Distinct().Take(60)) RuleWhen.Items.Add(n);
        }
        catch { }
    }
    void RuleSuggest_DropDown(object s, EventArgs e)
    {
        RuleSuggest.Items.Clear();
        foreach (var n in Buttons.List().Where(b => _fg == null || string.Equals(b.Process, _fg.Process, StringComparison.OrdinalIgnoreCase)).Select(b => b.Name).Where(n => n.Length > 0).Distinct()) RuleSuggest.Items.Add(n);
    }
    void RulePreset_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement f && f.Tag is string t && t.Split('|') is { Length: 2 } p) { RuleWhen.Text = p[0]; RuleSuggest.Text = p[1]; RuleSuggest.Focus(); } }
    void RuleAdd_Click(object s, RoutedEventArgs e)
    {
        var w = RuleWhen.Text.Trim(); var g = RuleSuggest.Text.Trim();
        if (w.Length < 2 || g.Length == 0) { RuleWhen.Focus(); RuleWhen.ToolTip = "«Вижу» — минимум 2 символа, «предложить» — не пусто."; return; }
        Rules.Add(new Rule { When = w, Suggest = g, Process = RuleThisApp.IsChecked == true && _fg != null ? _fg.Process : "" });
        RuleWhen.Text = RuleSuggest.Text = ""; LoadRules(); Rules.Sync();
    }
    void RuleToggle_Click(object s, RoutedEventArgs e) { if (RuleList.SelectedItem is Rule r) { r.Enabled = !r.Enabled; Rules.Save(); LoadRules(); Rules.Sync(); } }
    void RuleDel_Click(object s, RoutedEventArgs e) { if (RuleList.SelectedItem is Rule r) { Rules.Remove(r); LoadRules(); Rules.Sync(); } }
    void AppOffChk_Click(object s, RoutedEventArgs e)
    {
        if (_fg == null) return; var l = App.Current.Settings.ActDisabledApps;
        l.RemoveAll(a => string.Equals(a, _fg.Process, StringComparison.OrdinalIgnoreCase));
        if (AppOffChk.IsChecked == true) l.Add(_fg.Process);
        App.Current.Settings.Save(); LoadNt();
    }
    void BtnToggle_Click(object s, RoutedEventArgs e)
    {
        if (BtnList.SelectedItem is not LearnedButton b) { LearnInfo.Text = "Выбери кнопку в списке."; return; }
        b.Enabled = !b.Enabled; Buttons.Update(); LoadButtons(); LearnInfo.Text = $"«{b.Label}»: {(b.Enabled ? "вкл" : "выкл")} для «действовать».";
    }
    void SoundChk_Click(object s, RoutedEventArgs e) { App.Current.Settings.ClickSound = SoundChk.IsChecked == true; App.Current.Settings.Save(); }
    void LoadStartup() { StartList.ItemsSource = null; StartList.ItemsSource = App.Current.Settings.StartupActions.ToList(); }
    void StartChk_Click(object s, RoutedEventArgs e) { App.Current.Settings.StartupOptIn = StartChk.IsChecked == true; App.Current.Settings.Save(); }
    void StartAdd_Click(object s, RoutedEventArgs e)
    {
        var arg = StartArg.Text.Trim(); if (arg.Length == 0) return;
        var kind = new[] { "LaunchExe", "OpenUrl", "RunButton", "Wait" }[Math.Max(0, StartKind.SelectedIndex)];
        // v0.7.4: validate on add with the same LAUNCH/OPEN policy used at run time
        if (kind == "LaunchExe" && Actions.LaunchProblem(arg, false) is { } bad) { StartArg.ToolTip = bad; StartArg.Focus(); App.Current.Toast("Aura", bad); return; }
        if (kind == "OpenUrl" && !Actions.UrlOk(arg)) { App.Current.Toast("Aura", "Только http/https ссылки."); StartArg.Focus(); return; }
        if (kind == "Wait" && !int.TryParse(arg, out _)) { App.Current.Toast("Aura", "Ожидание — число секунд (1–30)."); StartArg.Focus(); return; }
        App.Current.Settings.StartupActions.Add(new StartupAction { Kind = kind, Arg = arg }); App.Current.Settings.Save(); StartArg.Text = ""; LoadStartup();
    }
    void StartDel_Click(object s, RoutedEventArgs e)
    {
        if (StartList.SelectedItem is not StartupAction a) return;
        App.Current.Settings.StartupActions.Remove(a); App.Current.Settings.Save(); LoadStartup();
    }
    async void StartRun_Click(object s, RoutedEventArgs e)
    {
        _acting = true;
        try { SafeClose(); await Actions.RunStartup(manual: true); } finally { _acting = false; }
    }

    // Deferred, idempotent close: safe from any event handler.
    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { try { Close(); } catch (InvalidOperationException) { } }));
    }

    void Close_Click(object s, RoutedEventArgs e) => SafeClose();
}
