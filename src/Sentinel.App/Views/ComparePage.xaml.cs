using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class ComparePage : SentinelPage
{
    public CompareViewModel Vm { get; } = Create<CompareViewModel>();

    public ComparePage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompareViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }
}
