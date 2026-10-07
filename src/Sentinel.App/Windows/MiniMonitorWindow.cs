using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Sentinel.App.Services;

namespace Sentinel.App.Windows;

/// <summary>
/// Compact live monitor that can be left open while working. Not always-on-top unless the user pins it.
/// Updates every two seconds and pauses while minimised.
/// </summary>
public sealed partial class MiniMonitorWindow : Window
{
    private readonly Grid _rows = new() { RowSpacing = 6, ColumnSpacing = 12 };
    private readonly DispatcherQueueTimer _timer;

    public MiniMonitorWindow()
    {
        Title = "Sentinel mini monitor";
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        WindowHelper.SetIcon(this);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsResizable = false;
        }
        WindowHelper.Resize(this, 260, 230);

        var pin = new ToggleButton { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right };
        ToolTipService.SetToolTip(pin, "Keep on top");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pin, "Keep on top");
        pin.Click += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter op) op.IsAlwaysOnTop = pin.IsChecked == true;
        };
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var root = new Grid { Padding = new Thickness(16, 8, 16, 14), RowSpacing = 6 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(pin);
        Grid.SetRow(_rows, 1);
        root.Children.Add(_rows);
        Content = root;
        (App.Services.GetService(typeof(ThemeService)) as ThemeService)?.Apply(root);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Render();
        AppWindow.Changed += (_, e) =>
        {
            if (!e.DidPresenterChange) return;
            var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (minimized) _timer.Stop();
            else _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        Render();
        _timer.Start();
    }

    private void Render()
    {
        _rows.Children.Clear();
        _rows.RowDefinitions.Clear();
        var i = 0;
        foreach (var r in LiveSummary.Rows().Where(r => r.Label != "Network"))
        {
            _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = r.Label == "Battery" ? "BAT" : r.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
            var primary = new TextBlock { Text = r.Primary, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var secondary = new TextBlock { Text = r.Label == "RAM" ? "" : r.Secondary.Split(" • ")[0], Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, i);
            Grid.SetRow(primary, i);
            Grid.SetRow(secondary, i);
            Grid.SetColumn(primary, 1);
            Grid.SetColumn(secondary, 2);
            label.VerticalAlignment = VerticalAlignment.Center;
            _rows.Children.Add(label);
            _rows.Children.Add(primary);
            _rows.Children.Add(secondary);
            i++;
        }
    }
}
