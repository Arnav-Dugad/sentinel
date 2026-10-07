using Microsoft.UI.Xaml;
using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class NetworkPage : SentinelPage
{
    public NetworkViewModel Vm { get; } = Create<NetworkViewModel>();

    public NetworkPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NetworkViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }

    private void OnTestClick(object sender, RoutedEventArgs e) => App.Current?.MainWindow?.NavigateTo("NetworkTest");
}
