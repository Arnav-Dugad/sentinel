using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class BatteryPage : SentinelPage
{
    public BatteryViewModel Vm { get; } = Create<BatteryViewModel>();

    public BatteryPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BatteryViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
            if (e.PropertyName == nameof(BatteryViewModel.CapacityChart) && Vm.CapacityChart is { } c) CapacityChart.Apply(c);
        };
    }
}
