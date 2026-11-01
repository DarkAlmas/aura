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
        Label.Text = (paused ? "Пауза" : $"Смотрю · {app}") + (Learn.Recording ? $"  ●  Учусь · {Learn.Current!.Steps.Count} шагов" : "");
        Dot.Fill = Learn.Recording ? Brushes.Red : paused ? (Brush)FindResource("Muted") : (Brush)FindResource("Accent");
    }
    public void SetDoing(string what)
    {
        Label.Text = $"Делаю · {what}";
        Dot.Fill = Brushes.OrangeRed;
    }
    void Pause_Click(object s, RoutedEventArgs e) => App.Current.TogglePause();
}
