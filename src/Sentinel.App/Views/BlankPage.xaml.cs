using Microsoft.UI.Xaml.Controls;

namespace Sentinel.App.Views;

/// <summary>Empty page shown while the window is hidden, so the previous page is properly deactivated and released.</summary>
public sealed partial class BlankPage : Page
{
    public BlankPage()
    {
        InitializeComponent();
    }
}
