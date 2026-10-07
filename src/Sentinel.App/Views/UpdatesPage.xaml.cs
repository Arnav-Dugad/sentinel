using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class UpdatesPage : SentinelPage
{
    public UpdatesViewModel Vm { get; } = Create<UpdatesViewModel>();

    public UpdatesPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }
}
