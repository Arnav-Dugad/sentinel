using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class AskPage : SentinelPage
{
    public AskViewModel Vm { get; } = Create<AskViewModel>();

    public AskPage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Loaded += (_, _) => QuestionBox.Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        Vm.AskCommand.Execute(null);
    }

    private void OnSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string text }) Vm.UseSuggestionCommand.Execute(text);
    }
}
