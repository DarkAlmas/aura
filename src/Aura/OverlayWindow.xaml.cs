using System;
using System.IO;
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
        _fg = fg;
        Ctx.Text = "СЕЙЧАС · " + Kind(fg?.Process).ToUpperInvariant();
        NextText.Text = Suggest(fg?.Process);
        Status.Text = App.Current.Settings.ScreenCaptureOptIn ? "OCR: opt-in (v1 не снимает)" : "OCR выкл.";
        Loaded += (_, _) => { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth - 8; Top = wa.Bottom - ActualHeight - 8; };
        Deactivated += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
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
        Close();
    }

    void Note_Click(object s, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aura");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "notes.txt");
        File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {_fg?.Process ?? "-"} — {_fg?.Title ?? "-"}{Environment.NewLine}");
        System.Diagnostics.Process.Start("notepad.exe", $"\"{file}\"");
        Close();
    }

    void Timer_Click(object s, RoutedEventArgs e)
    {
        int min = int.Parse((string)((FrameworkElement)s).Tag);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(min) };
        t.Tick += (_, _) => { t.Stop(); App.Current.Toast("Aura · таймер", $"{min} мин прошло."); };
        t.Start();
        App.Current.Toast("Aura · таймер", $"Запущен на {min} мин.");
        Close();
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();
}
