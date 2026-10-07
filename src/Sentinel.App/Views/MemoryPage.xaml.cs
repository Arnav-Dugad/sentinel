using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class MemoryPage : SentinelPage
{
    public MemoryViewModel Vm { get; } = Create<MemoryViewModel>();

    public MemoryPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MemoryViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }
}
