using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class GpuPage : SentinelPage
{
    public GpuViewModel Vm { get; } = Create<GpuViewModel>();

    public GpuPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GpuViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }
}
