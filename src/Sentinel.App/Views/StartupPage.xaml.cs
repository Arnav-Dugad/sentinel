using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class StartupPage : SentinelPage
{
    public StartupViewModel Vm { get; } = Create<StartupViewModel>();

    public StartupPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
