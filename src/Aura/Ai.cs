using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Aura;

/// v0.5.0 «Спросить ИИ» — the same OpenAI-compatible backends as circuit-sim:
/// free Groq proxy https://circuit-sim-ai-proxy.vercel.app/v1 (no key in the app) or local Ollama http://127.0.0.1:11434/v1.
/// Network ONLY when the user ticked «ИИ (сеть)» (AiOptIn) and pressed «Спросить». Sends: app name, window title,
/// learned button names, the user's request, and — only if «приложить текст окна» is ticked — OCR text of the window (no image).
public static class Ai
{
    public const string ProxyBase = "https://circuit-sim-ai-proxy.vercel.app/v1";
    public const string OllamaBase = "http://127.0.0.1:11434/v1";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public sealed record Intent(string Kind, string Arg)
    {
        public override string ToString() => Kind switch
        {
            "CLICK" => $"Нажать «{Arg}»", "DOUBLECLICK" => $"Двойной клик «{Arg}»", "RIGHTCLICK" => $"ПКМ «{Arg}»",
            "FIND" => $"Найти «{Arg}»", "REMEMBER" => $"Запомнить «{Arg}»", "LAUNCH" => $"Запустить {Arg}", "OPEN" => $"Открыть {Arg}",
            "WAIT" => $"Подождать {Arg} с", _ => $"{Kind} {Arg}"
        };
    }

    const string System = @"Ты — Aura, помощник по Windows на ПК пользователя. Отвечай по-русски, кратко (1-3 предложения).
Ты НЕ нажимаешь сам: предлагаешь действия, пользователь подтверждает, Aura выполняет с отсчётом «Делаю» 1 с.
Если нужно действие — в КОНЦЕ ответа добавь строки команд, по одной в строке, ровно в таком виде:
CLICK: <точное имя кнопки>
DOUBLECLICK: <имя>
RIGHTCLICK: <имя>
FIND: <текст кнопки>
REMEMBER: <имя кнопки>
LAUNCH: <путь к .exe или имя программы, напр. notepad>
OPEN: <https://url>
WAIT: <секунды 1-10>
Используй имена из списка выученных кнопок или из текста окна. Не выдумывай пути. Никаких других форматов команд.";

    public static async Task<(string Text, List<Intent> Intents)> Ask(string request, Native.WindowInfo? fg, string? windowText)
    {
        var st = App.Current.Settings;
        if (!st.AiOptIn) return ("ИИ выключен: включи «ИИ (сеть)».", new());
        var learned = Buttons.List().Where(b => fg == null || string.Equals(b.Process, fg.Process, StringComparison.OrdinalIgnoreCase)).Select(b => b.Name).Distinct().Take(60).ToList();
        var ctx = new Dictionary<string, object?>
        {
            ["app"] = fg?.Process, ["title"] = fg == null || st.IsExcluded(fg.Process) ? null : fg.Title,
            ["learned_buttons"] = learned,
            ["all_learned"] = Buttons.List().Select(b => b.Process + ": " + b.Name).Distinct().Take(80).ToList(),
        };
        if (!string.IsNullOrWhiteSpace(windowText)) ctx["window_text"] = windowText!.Length > 3000 ? windowText[..3000] : windowText;
        var body = new
        {
            model = string.IsNullOrWhiteSpace(st.AiModel) ? "openai/gpt-oss-20b" : st.AiModel,
            temperature = 0.2, max_tokens = 1024, stream = false,
            messages = new object[]
            {
                new { role = "system", content = System },
                new { role = "user", content = "Контекст: " + JsonSerializer.Serialize(ctx) + "\nЗапрос: " + request }
            }
        };
        var baseUrl = string.IsNullOrWhiteSpace(st.AiBaseUrl) ? ProxyBase : st.AiBaseUrl.TrimEnd('/');
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(st.AiKey)) req.Headers.Add("Authorization", "Bearer " + st.AiKey);
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return ($"ИИ ответил ошибкой {(int)resp.StatusCode}. {Trim(json, 200)}", new());
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var intents = Parse(text);
            var clean = string.Join("\n", text.Split('\n').Where(l => !Rx.IsMatch(l))).Trim();
            return (clean.Length > 0 ? clean : "Готово — подтверди действия ниже.", intents);
        }
        catch (TaskCanceledException) { return ("ИИ не ответил за 60 с (нет сети?).", new()); }
        catch (Exception ex) { return ("ИИ недоступен: " + ex.Message, new()); }
    }

    static readonly Regex Rx = new(@"^\s*[-*]?\s*`?(CLICK|DOUBLECLICK|RIGHTCLICK|FIND|REMEMBER|LAUNCH|OPEN|WAIT)\s*:\s*(.+?)`?\s*$", RegexOptions.IgnoreCase);
    public static List<Intent> Parse(string text) =>
        text.Split('\n').Select(l => Rx.Match(l)).Where(m => m.Success)
            .Select(m => new Intent(m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Trim().Trim('«', '»', '"', '\'')))
            .Where(i => i.Arg.Length > 0).Take(8).ToList();
    static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
