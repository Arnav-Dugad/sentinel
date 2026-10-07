using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class NetworkTestPage : SentinelPage
{
    public NetworkTestViewModel Vm { get; } = Create<NetworkTestViewModel>();

    public NetworkTestPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
