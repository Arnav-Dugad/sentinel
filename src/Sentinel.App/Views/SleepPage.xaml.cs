using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class SleepPage : SentinelPage
{
    public SleepViewModel Vm { get; } = Create<SleepViewModel>();

    public SleepPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
