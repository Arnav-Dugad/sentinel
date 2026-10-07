using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class DriversPage : SentinelPage
{
    public DriversViewModel Vm { get; } = Create<DriversViewModel>();

    public DriversPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
