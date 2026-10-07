using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sentinel.App.ViewModels;
using Sentinel.Telemetry;

namespace Sentinel.App.Views;

/// <summary>Base page: activates its view model while shown and asks the engine to sample its category at detail rate.</summary>
public partial class SentinelPage : Page
{
    protected PageViewModel? ViewModel { get; set; }

    protected static T Create<T>() where T : class => ActivatorUtilities.CreateInstance<T>(App.Services);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var info = PageRegistry.Find(GetType());
        App.Services.GetRequiredService<TelemetryEngine>().SetDetailCategory(info?.DetailCategory);
        ViewModel?.Activate(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel?.Deactivate();
    }
}
