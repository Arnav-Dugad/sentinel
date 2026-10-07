using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class CpuPage : SentinelPage
{
    public CpuViewModel Vm { get; } = Create<CpuViewModel>();

    public CpuPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CpuViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }
}
