using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sentinel.App.ViewModels;
using Sentinel.Telemetry;

namespace Sentinel.App.Views;

/// <summary>Base page: activates its view model while shown and asks the engine to sample its category at detail rate.</summary>
public partial class SentinelPage : Page
{
    private bool _staggered;

    protected PageViewModel? ViewModel { get; set; }

    protected static T Create<T>() where T : class => ActivatorUtilities.CreateInstance<T>(App.Services);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var info = PageRegistry.Find(GetType());
        App.Services.GetRequiredService<TelemetryEngine>().SetDetailCategory(info?.DetailCategory);
        // Sections rise in one after another as the page opens (skipped when motion is reduced).
        if (!_staggered && FindSections() is { } sections)
        {
            _staggered = true;
            Controls.Motion.StaggerChildren(sections);
        }
        ViewModel?.Activate(e.Parameter);
    }

    /// <summary>The page's section stack: the panel inside its top-level ScrollViewer (directly, or inside a root layout grid).</summary>
    private Panel? FindSections() => Content switch
    {
        ScrollViewer { Content: Panel p } => p,
        Panel root => root.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content as Panel,
        _ => null,
    };

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel?.Deactivate();
    }
}
