using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class PerformancePage : SentinelPage
{
    public PerformanceViewModel Vm { get; } = Create<PerformanceViewModel>();

    public PerformancePage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
