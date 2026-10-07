using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class ThermalsPage : SentinelPage
{
    public ThermalsViewModel Vm { get; } = Create<ThermalsViewModel>();

    public ThermalsPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThermalsViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }
}
