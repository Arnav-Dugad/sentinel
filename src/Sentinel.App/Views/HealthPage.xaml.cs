using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class HealthPage : SentinelPage
{
    public HealthViewModel Vm { get; } = Create<HealthViewModel>();

    public HealthPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
