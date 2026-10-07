using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class ProcessesPage : SentinelPage
{
    public ProcessesViewModel Vm { get; } = Create<ProcessesViewModel>();

    public ProcessesPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }

    private void OnSort(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: string key }) Vm.SortBy = key;
    }
}
