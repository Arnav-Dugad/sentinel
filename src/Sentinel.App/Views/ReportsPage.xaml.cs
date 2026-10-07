using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class ReportsPage : SentinelPage
{
    public ReportsViewModel Vm { get; } = Create<ReportsViewModel>();

    public ReportsPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
