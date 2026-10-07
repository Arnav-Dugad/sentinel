using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class StoragePage : SentinelPage
{
    public StorageViewModel Vm { get; } = Create<StorageViewModel>();

    public StoragePage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StorageViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
        };
    }
}
