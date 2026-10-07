using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class SettingsPage : SentinelPage
{
    public SettingsViewModel Vm { get; } = Create<SettingsViewModel>();

    public SettingsPage()
    {
        ViewModel = Vm;
        InitializeComponent();
    }

    private async void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete all history?",
            Content = "This permanently deletes recorded telemetry, events, baselines, sessions and change history from this PC. Live monitoring continues.",
            PrimaryButtonText = "Delete history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Vm.ClearHistoryAsync();
    }
}
