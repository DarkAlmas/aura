using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aura;

/// Always-visible «Смотрю» pill while a session (overlay) is active.
public partial class LookIndicator : Window
{
    public LookIndicator()
    {
        InitializeComponent();
        Loaded += (_, _) => { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth - 8; Top = wa.Top + 8; };
        var pulse = new DoubleAnimation(1, 0.35, System.TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Dot.BeginAnimation(OpacityProperty, pulse);
    }
    public void SetState(string app, bool paused)
    {
        var st = App.Current.Settings;
        Label.Text = (paused ? "Пауза" : $"Смотрю · {app}") + (App.Current.Learning ? "  ●  Учусь" : "") + (!paused && Watcher.On ? "  ·  действую" : "");
        Dot.Fill = App.Current.Learning ? Brushes.Red : paused ? (Brush)FindResource("Muted") : (Brush)FindResource("Accent");
        StopBtn.Visibility = Visibility.Collapsed;
    }
    public void SetDoing(string what, bool stoppable = false)
    {
        Label.Text = $"Делаю · {what}";
        Dot.Fill = Brushes.OrangeRed;
        StopBtn.Visibility = stoppable ? Visibility.Visible : Visibility.Collapsed;
    }
    void Stop_Click(object s, RoutedEventArgs e) => App.Current.ActionCts?.Cancel();
    void Pause_Click(object s, RoutedEventArgs e) => App.Current.TogglePause();
}
