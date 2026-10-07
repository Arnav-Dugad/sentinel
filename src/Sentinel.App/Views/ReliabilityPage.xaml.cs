using Microsoft.UI.Xaml;
using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class ReliabilityPage : SentinelPage
{
    public ReliabilityViewModel Vm { get; } = Create<ReliabilityViewModel>();

    public ReliabilityPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReliabilityViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }

    private void OnTimeline(object sender, RoutedEventArgs e) => App.Current?.MainWindow?.NavigateTo("Timeline");
}
