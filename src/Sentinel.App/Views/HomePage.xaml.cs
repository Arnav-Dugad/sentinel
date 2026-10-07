using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class HomePage : SentinelPage
{
    public HomeViewModel Vm { get; } = Create<HomeViewModel>();

    public HomePage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) App.Current?.MainWindow?.NavigateTo(tag);
    }

    private void OnTimelineClick(object sender, RoutedEventArgs e) => App.Current?.MainWindow?.NavigateTo("Timeline");

    private void OnHealthClick(object sender, RoutedEventArgs e) => App.Current?.MainWindow?.NavigateTo("Health");

    private void OnProcessesClick(object sender, RoutedEventArgs e) => App.Current?.MainWindow?.NavigateTo("Processes");
}
