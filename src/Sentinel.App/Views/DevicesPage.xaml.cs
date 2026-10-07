using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Sentinel.App.Controls;
using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class DevicesPage : SentinelPage
{
    public DevicesViewModel Vm { get; } = Create<DevicesViewModel>();

    public DevicesPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.DisplaysChanged += (_, _) => DrawTopology();
        Topology.SizeChanged += (_, _) => DrawTopology();
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        DevicesPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisplaysPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ComputerPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        Vm.Section = Math.Max(0, index);
        if (index == 1) DrawTopology();
    }

    /// <summary>Draws displays scaled to fit, positioned as Windows arranges the desktop.</summary>
    private void DrawTopology()
    {
        Topology.Children.Clear();
        var displays = Vm.Displays.Select(d => d.Info).ToList();
        if (displays.Count == 0 || Topology.ActualWidth < 20) return;
        double minX = displays.Min(d => d.PositionX), minY = displays.Min(d => d.PositionY);
        double maxX = displays.Max(d => d.PositionX + d.Width), maxY = displays.Max(d => d.PositionY + d.Height);
        var scale = Math.Min((Topology.ActualWidth - 20) / (maxX - minX), (Topology.ActualHeight - 20) / (maxY - minY));
        var offsetX = (Topology.ActualWidth - (maxX - minX) * scale) / 2;
        var offsetY = (Topology.ActualHeight - (maxY - minY) * scale) / 2;
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var surface = (Brush)Application.Current.Resources["SurfaceSecondaryBrush"];
        var border = (Brush)Application.Current.Resources["BorderSubtleBrush"];
        var i = 1;
        foreach (var d in displays)
        {
            var w = d.Width * scale - 6;
            var h = d.Height * scale - 6;
            var rect = new Border
            {
                Width = Math.Max(20, w),
                Height = Math.Max(16, h),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(d.IsPrimary ? 2 : 1),
                BorderBrush = d.IsPrimary ? accent : border,
                Background = surface,
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = i.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = d.FriendlyName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(20, w - 12), HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = $"{d.Width}×{d.Height} · {d.RefreshHz:0} Hz", FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            ToolTipService.SetToolTip(rect, $"{d.FriendlyName}\n{d.Connection}{(d.GpuName is null ? "" : "\nDriven by " + d.GpuName)}");
            Canvas.SetLeft(rect, offsetX + (d.PositionX - minX) * scale + 3);
            Canvas.SetTop(rect, offsetY + (d.PositionY - minY) * scale + 3);
            Topology.Children.Add(rect);
            i++;
        }
    }
}
