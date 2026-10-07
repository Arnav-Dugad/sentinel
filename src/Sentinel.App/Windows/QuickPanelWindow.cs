using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sentinel.App.Services;

namespace Sentinel.App.Windows;

/// <summary>Small flyout-style window shown from the tray icon. Closes when it loses focus.</summary>
public sealed partial class QuickPanelWindow : Window
{
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherQueueTimer _timer;
    private bool _activatedOnce;

    public QuickPanelWindow()
    {
        Title = "Sentinel";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(true, false);
        }
        WindowHelper.Resize(this, 320, 330);

        var open = new Button { Content = "Open Sentinel", HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        open.Click += (_, _) =>
        {
            App.Current?.ShowMainWindow();
            Close();
        };
        var header = new TextBlock { Text = "Sentinel", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var root = new Grid { Padding = new Thickness(20, 16, 20, 16), RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(header);
        Grid.SetRow(_rows, 1);
        root.Children.Add(_rows);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        Grid.SetRow(open, 3);
        root.Children.Add(open);
        Content = root;
        (App.Services.GetService(typeof(ThemeService)) as ThemeService)?.Apply(root);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Render();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (_activatedOnce) Close();
            }
            else
            {
                _activatedOnce = true;
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            IsOpen = false;
        };
    }

    public bool IsOpen { get; private set; }

    public void ShowNearTray()
    {
        Render();
        WindowHelper.PlaceNearTray(this);
        IsOpen = true;
        _timer.Start();
        WindowHelper.BringToFront(this);
    }

    private void Render()
    {
        _rows.Children.Clear();
        foreach (var r in LiveSummary.Rows())
        {
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = r.Glyph, FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
            var label = new TextBlock { Text = r.Label, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
            var primary = new TextBlock { Text = r.Primary, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var secondary = new TextBlock { Text = r.Secondary, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(primary, 2);
            Grid.SetColumn(secondary, 3);
            g.Children.Add(icon);
            g.Children.Add(label);
            g.Children.Add(primary);
            g.Children.Add(secondary);
            _rows.Children.Add(g);
        }
        _status.Text = LiveSummary.StatusLine();
    }
}
