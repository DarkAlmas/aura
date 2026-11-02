using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Aura;

/// v0.5.0 «При старте» item: LaunchExe | OpenUrl | RunButton (name of a learned button) | Wait (seconds).
public sealed class StartupAction
{
    public string Kind { get; set; } = "LaunchExe";
    public string Arg { get; set; } = "";
    public override string ToString() => Kind switch
    {
        "LaunchExe" => "▶ Запустить: " + Arg, "OpenUrl" => "🌐 Открыть: " + Arg, "RunButton" => "🖱 Нажать: «" + Arg + "»",
        "Wait" => "⏱ Ждать " + Arg + " с", _ => Kind + " " + Arg
    };
}

/// Executes one confirmed intent (from AI or «При старте»/«Серия»). Every click goes through Buttons.DoWithCountdown («Делаю» 1 с + Стоп).
public static class Actions
{
    public static async Task<string> Run(Ai.Intent it, Native.WindowInfo? fg, bool guardLeft = false, bool fromAi = false)
    {
        var st = App.Current.Settings;
        if (st.Paused) return "Пауза — не выполняю.";
        switch (it.Kind)
        {
            case "LAUNCH": return Launch(it.Arg, fromAi);
            case "OPEN": return Open(it.Arg);
            case "RULE":
                {
                    var parts = it.Arg.Split(new[] { "=>", "→", "->" }, 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return "Правило не понял (нужно «вижу => нажать»).";
                    Rules.Add(new Rule { When = parts[0], Suggest = parts[1], Process = fg?.Process ?? "" }); Rules.Sync();
                    return $"Добавил правило: вижу «{parts[0]}» → предложить «{parts[1]}» (только подсказка).";
                }
            case "WAIT": if (int.TryParse(it.Arg, out var sec)) await Task.Delay(Math.Clamp(sec, 1, 30) * 1000); return "Подождал.";
        }
        if (fg == null || !Native.IsWindow(fg.Hwnd)) return "Нет окна для действия.";
        if (st.IsExcluded(fg.Process)) return "Приложение в исключениях.";
        var hit = await Locate(it.Arg, fg);
        if (hit == null) return $"Не нашёл «{it.Arg}» в {fg.Process}.";
        var learned = Buttons.ByName(it.Arg, fg.Process);
        switch (it.Kind)
        {
            case "FIND": App.Current.Toast("Aura", $"Нашёл «{hit.Label}» ({hit.Type})."); return $"Нашёл «{hit.Label}».";
            case "REMEMBER":
                var b = Buttons.Remember(fg.Process, hit, ClickAction.Click, st.SaveTemplates);
                return b == null ? "Не запомнил." : $"Запомнил «{b.Label}».";
            default:
                var act = it.Kind == "DOUBLECLICK" ? ClickAction.DoubleClick : it.Kind == "RIGHTCLICK" ? ClickAction.RightClick : learned?.Action ?? ClickAction.Click;
                var how = await Buttons.DoWithCountdown(hit, act, fg.Hwnd, guardLeft: guardLeft);
                return how == null ? "Остановлено." : $"Нажал «{hit.Label}» ({how}).";
        }
    }

    public static async Task<UiaFinder.Target?> Locate(string name, Native.WindowInfo fg)
    {
        var uia = await Task.Run(() => UiaFinder.Enumerate(fg.Hwnd));
        var lb = Buttons.ByName(name, fg.Process);
        if (lb != null) { var h = await Buttons.Locate(lb, fg.Hwnd, uia); if (h != null) return h; }
        var m = UiaFinder.Match(uia, name);
        if (m.Count > 0) return m[0];
        if (App.Current.Settings.ScreenCaptureOptIn && OcrFinder.Available)
        {
            var o = await OcrFinder.Find(fg.Hwnd, name, System.Windows.Rect.Empty);
            if (o != null && o.Count > 0) return o[0];
        }
        return null;
    }

    static readonly string[] LaunchExt = { ".exe", ".bat", ".cmd" };
    /// v0.5.5 (Hack WARN): only an existing local .exe/.bat/.cmd by full path, no «..», no URL schemes / UNC.
    /// From AI it must additionally be already approved by the user as a «При старте → Запустить» entry.
    public static string? LaunchProblem(string path, bool fromAi)
    {
        var p = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
        if (p.Length == 0) return "Пустой путь.";
        if (p.Contains("..") || p.Contains("://") || p.StartsWith(@"\\") || p.StartsWith("//") || p.IndexOfAny(new[] { '|', '&', '<', '>', '^' }) >= 0) return $"Отказ: недопустимый путь «{p}».";
        if (!System.IO.Path.IsPathFullyQualified(p)) return $"Отказ: нужен полный путь, а не «{p}».";
        if (!LaunchExt.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant())) return $"Отказ: запускаю только .exe/.bat/.cmd («{p}»).";
        if (!System.IO.File.Exists(p)) return $"Отказ: файла нет «{p}».";
        if (fromAi && !App.Current.Settings.StartupActions.Any(a => a.Kind == "LaunchExe" && string.Equals(Environment.ExpandEnvironmentVariables(a.Arg.Trim().Trim('"')), p, StringComparison.OrdinalIgnoreCase)))
            return $"Отказ: ИИ может запускать только программы из твоего списка «При старте → Запустить». Добавь «{p}» туда сам.";
        return null;
    }
    public static string Launch(string path, bool fromAi = false)
    {
        var bad = LaunchProblem(path, fromAi);
        if (bad != null) { App.Current.Toast("Aura", bad); return bad; }
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try { Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(path.Trim('"'))) { UseShellExecute = true }); return "Запустил " + path; }
        catch (Exception ex) { return "Не запустил: " + ex.Message; }
    }
    public static string Open(string url)
    {
        url = (url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(u.UserInfo) || url.Any(char.IsWhiteSpace))
        { var m = $"Отказ: открываю только http/https ссылки («{url}»)."; App.Current.Toast("Aura", m); return m; }
        url = u.AbsoluteUri;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return "Открыл " + url; }
        catch (Exception ex) { return "Не открыл: " + ex.Message; }
    }

    /// «При старте»: only if StartupOptIn. 2 s after launch, toast, then each item in order. Clicks still use «Делаю» 1 с + Стоп.
    public static async Task RunStartup()
    {
        var st = App.Current.Settings;
        if (!st.StartupOptIn || st.StartupActions.Count == 0) return;
        await Task.Delay(2000);
        if (st.Paused) return;
        App.Current.Toast("Aura · При старте", $"Выполняю {st.StartupActions.Count} действ. (Пауза — отмена).");
        StopAll = false;
        foreach (var a in st.StartupActions.ToList())
        {
            if (App.Current.Settings.Paused || StopAll) return;
            var it = a.Kind switch { "LaunchExe" => new Ai.Intent("LAUNCH", a.Arg), "OpenUrl" => new Ai.Intent("OPEN", a.Arg), "Wait" => new Ai.Intent("WAIT", a.Arg), _ => new Ai.Intent("CLICK", a.Arg) };
            var fg = Native.Foreground();
            await Run(it, fg != null && fg.Pid != Environment.ProcessId ? fg : null);
            await Task.Delay(Math.Clamp(st.StepDelayMs, 200, 10000));
        }
    }

    /// «Серия»: run selected learned buttons one after another in the current foreground window, each with «Делаю».
    /// v0.5.3: keeps running if the overlay is closed; always ends with a tray toast «Серия: k из n» (stopped/left/paused are explicit).
    /// v0.5.6: tray «Стоп всё» — cancels the current «Делаю» and all remaining series/startup/AI steps.
    public static volatile bool StopAll;
    public static void Stop() { StopAll = true; App.Current.ActionCts?.Cancel(); App.Current.Toast("Aura", "Стоп: текущее действие и серия отменены."); }
    public static async Task<string> RunSeries(IEnumerable<LearnedButton> list, Native.WindowInfo fg)
    {
        var l = list.ToList(); int done = 0; string why = ""; StopAll = false;
        foreach (var b in l)
        {
            if (StopAll) { why = " — Стоп"; break; }
            if (App.Current.Settings.Paused) { why = " — пауза"; break; }
            if (!Native.IsWindow(fg.Hwnd)) { why = " — окно закрыто"; break; }
            var r = await Run(new Ai.Intent(b.Action == ClickAction.DoubleClick ? "DOUBLECLICK" : b.Action == ClickAction.RightClick ? "RIGHTCLICK" : "CLICK", b.Name), fg, guardLeft: true);
            if (r.StartsWith("Остановлено")) { why = " — остановлено/ушёл из окна"; break; }
            if (r.StartsWith("Нажал")) done++;
            await Task.Delay(Math.Clamp(App.Current.Settings.StepDelayMs, 200, 10000));
        }
        var msg = $"Серия: {done} из {l.Count}{why}.";
        App.Current.Toast("Aura · Серия", msg); return msg;
    }
}
