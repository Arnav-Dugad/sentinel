using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class AnomaliesPage : SentinelPage
{
    public AnomaliesViewModel Vm { get; } = Create<AnomaliesViewModel>();

    public AnomaliesPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
