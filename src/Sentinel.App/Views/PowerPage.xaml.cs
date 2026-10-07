using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class PowerPage : SentinelPage
{
    public PowerViewModel Vm { get; } = Create<PowerViewModel>();

    public PowerPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
