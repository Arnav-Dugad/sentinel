using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Sentinel.App.Services;
using Sentinel.App.Views;
using Sentinel.Core.Settings;
using Sentinel.Intelligence;
using Sentinel.Telemetry;
using Windows.Graphics;

namespace Sentinel.App.Windows;

public sealed record SearchSuggestion(string Title, string Subtitle, string Glyph, string Kind, string Target, object? Parameter);

public sealed partial class MainWindow : Window
{
    private readonly TelemetryEngine _engine = App.Services.GetRequiredService<TelemetryEngine>();
    private readonly UiClock _clock = App.Services.GetRequiredService<UiClock>();
    private readonly ISettingsStore _settings = App.Services.GetRequiredService<ISettingsStore>();
    private readonly MotionPolicy _motion = App.Services.GetRequiredService<MotionPolicy>();
    private readonly Updates.UpdateService _updates = App.Services.GetRequiredService<Updates.UpdateService>();

    public MainWindow()
    {
        InitializeComponent();
        Title = "Sentinel";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        WindowHelper.SetIcon(this);
        WindowHelper.Resize(this, 1320, 880);
        WindowHelper.Center(this);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 560;
            p.PreferredMinimumHeight = 480;
        }

        App.Services.GetRequiredService<ThemeService>().Apply(Root);
        BuildNavigation();

        var simulation = App.Current?.IsSimulation == true;
        SimulationBadge.Visibility = simulation ? Visibility.Visible : Visibility.Collapsed;
        SimulationBanner.IsOpen = simulation;

        AppWindow.Closing += OnClosing;
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPresenterChange || e.DidVisibilityChange || e.DidSizeChange) UpdateVisibility();
            if (e.DidSizeChange) UpdateTitleBarRegions();
        };
        VisibilityChanged += (_, _) => UpdateVisibility();
        Activated += (_, _) => UpdateVisibility();
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleBar.Loaded += (_, _) => UpdateTitleBarRegions();
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;
        _updates.ReadyChanged += OnUpdateReadyChanged;
        Closed += (_, _) => _updates.ReadyChanged -= OnUpdateReadyChanged;
        UpdateRecordingIndicator();
        ShowUpdatePill();
        NavigateTo("Home");
    }

    private void OnSettingsChanged(object? sender, SentinelSettings s) => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateRecordingIndicator();
        App.Services.GetRequiredService<ThemeService>().Apply(Root);
    });

    private void BuildNavigation()
    {
        var home = PageRegistry.Find("Home")!;
        Nav.MenuItems.Add(Item(home));
        foreach (var group in PageRegistry.Groups)
        {
            var parent = new NavigationViewItem
            {
                Content = group,
                SelectsOnInvoked = false,
                IsExpanded = true,
                Icon = new FontIcon { Glyph = group switch { "System" => "", "Intelligence" => "", "Software" => "", _ => "" } },
            };
            foreach (var p in PageRegistry.All.Where(p => p.Group == group)) parent.MenuItems.Add(Item(p));
            Nav.MenuItems.Add(parent);
        }
    }

    private static NavigationViewItem Item(PageInfo p)
    {
        var item = new NavigationViewItem { Content = p.Title, Tag = p.Tag, Icon = new FontIcon { Glyph = p.Glyph } };
        ToolTipService.SetToolTip(item, p.Description);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, p.Title);
        return item;
    }

    public void NavigateTo(string tag, object? parameter = null)
    {
        var info = PageRegistry.Find(tag);
        if (info is null) return;
        if (ContentFrame.CurrentSourcePageType == info.PageType && parameter is null) return;
        NavigationTransitionInfo transition = _motion.AnimationsEnabled ? new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo();
        ContentFrame.Navigate(info.PageType, parameter, transition);
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string tag) NavigateTo(tag);
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        var info = PageRegistry.Find(e.SourcePageType);
        if (info is null) return;
        // Keep keyboard focus in the content rather than leaving it in the search box.
        if (SearchBox.FocusState != FocusState.Unfocused && string.IsNullOrEmpty(SearchBox.Text)) Nav.Focus(FocusState.Programmatic);
        Nav.IsPaneVisible = info.Tag != "Welcome";
        var item = FindItem(Nav.MenuItems, info.Tag) ?? FindItem(Nav.FooterMenuItems, info.Tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private static NavigationViewItem? FindItem(IList<object> items, string tag)
    {
        foreach (var o in items)
        {
            if (o is not NavigationViewItem n) continue;
            if (n.Tag as string == tag) return n;
            if (FindItem(n.MenuItems, tag) is { } child) return child;
        }
        return null;
    }

    // ------------------------------------------------------------------ visibility & close-to-tray

    private string? _parkedPage;

    private void UpdateVisibility()
    {
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var visible = AppWindow.IsVisible && !minimized;
        _engine.SetVisible(visible);
        _clock.SetRunning(visible);
        if (visible && _parkedPage is { } page)
        {
            _parkedPage = null;
            NavigateTo(page);
        }
    }

    /// <summary>
    /// While Sentinel lives in the notification area, the page and its visual tree are released and the
    /// working set is trimmed, so the background footprint stays small. The page is rebuilt when shown.
    /// </summary>
    private void Park()
    {
        _parkedPage = PageRegistry.Find(ContentFrame.CurrentSourcePageType)?.Tag ?? "Home";
        ContentFrame.Navigate(typeof(BlankPage), null, new SuppressNavigationTransitionInfo());
        ContentFrame.BackStack.Clear();
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        TrimWorkingSet();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    private static void TrimWorkingSet()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Current?.ShouldHideOnClose == true)
        {
            args.Cancel = true;
            AppWindow.Hide();
            UpdateVisibility();
            // Navigation is not allowed inside the closing callback; release the page once it returns.
            DispatcherQueue.TryEnqueue(Park);
            return;
        }
        _engine.SetVisible(false);
        _clock.SetRunning(false);
        if (App.Current is { } app && !app.ShouldHideOnClose) _ = app.ExitAsync();
    }

    // ------------------------------------------------------------------ title bar

    private void UpdateTitleBarRegions()
    {
        if (TitleBar.XamlRoot is null) return;
        var scale = TitleBar.XamlRoot.RasterizationScale;
        CaptionInset.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale));
        RectInt32 Rect(FrameworkElement e)
        {
            var t = e.TransformToVisual(null).TransformBounds(new global::Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
            return new RectInt32((int)(t.X * scale), (int)(t.Y * scale), (int)(t.Width * scale), (int)(t.Height * scale));
        }
        try
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough,
                UpdateButton.Visibility == Visibility.Visible ? [Rect(SearchBox), Rect(UpdateButton), Rect(RecordingButton)] : [Rect(SearchBox), Rect(RecordingButton)]);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Older runtimes: the search box remains reachable with Ctrl+K.
        }
    }

    private void OnUpdateReadyChanged(object? sender, bool ready) => DispatcherQueue.TryEnqueue(ShowUpdatePill);

    private void ShowUpdatePill()
    {
        var show = _updates.IsReady && _updates.CanInstall;
        UpdateButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            UpdateText.Text = $"Update to {_updates.NewVersion}";
            ToolTipService.SetToolTip(UpdateButton, $"Sentinel {_updates.NewVersion} has been downloaded and its signature verified. Restart to install it.");
        }
        UpdateTitleBarRegions();
    }

    private void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = $"Sentinel {_updates.NewVersion} is ready", Style = (Style)Application.Current.Resources["SectionTitleTextStyle"] });
        panel.Children.Add(new TextBlock
        {
            Text = "Sentinel restarts in a few seconds. Your history, settings and baselines are kept.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["MutedTextStyle"],
        });
        if (!string.IsNullOrWhiteSpace(_updates.Notes))
        {
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                Content = new TextBlock { Text = _updates.Notes, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyTextStyle"] },
            });
        }
        var restart = new Button { Content = "Restart and update", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        restart.Click += (_, _) => _updates.RestartToUpdate();
        panel.Children.Add(restart);
        new Flyout { Content = panel }.ShowAt(UpdateButton);
    }

    private void UpdateRecordingIndicator()
    {
        var s = _settings.Current;
        var (text, brush) = s.PrivacyMode ? ("Privacy mode", "StatusAttentionBrush")
            : s.RecordingPaused ? ("Recording paused", "StatusUnknownBrush")
            : ("Recording", "StatusHealthyBrush");
        RecordingText.Text = text;
        RecordingDot.Fill = (Brush)Application.Current.Resources[brush];
        ToolTipService.SetToolTip(RecordingButton, s.PrivacyMode
            ? "Privacy mode: application names and process history are not collected. Hardware telemetry continues."
            : s.RecordingPaused ? "History recording is paused. Live readings continue but nothing is saved." : "Sentinel is recording history locally on this PC. Nothing is uploaded.");
    }

    private void OnRecordingClicked(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        var pause = new ToggleMenuFlyoutItem { Text = "Pause history recording", IsChecked = _settings.Current.RecordingPaused };
        pause.Click += (_, _) => _settings.Update(s => s.RecordingPaused = !s.RecordingPaused);
        var privacy = new ToggleMenuFlyoutItem { Text = "Privacy mode", IsChecked = _settings.Current.PrivacyMode };
        privacy.Click += (_, _) => _settings.Update(s => s.PrivacyMode = !s.PrivacyMode);
        var mini = new MenuFlyoutItem { Text = "Open mini monitor", Icon = new FontIcon { Glyph = "" } };
        mini.Click += (_, _) => App.Current?.ShowMiniMonitor();
        flyout.Items.Add(pause);
        flyout.Items.Add(privacy);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(mini);
        flyout.ShowAt(RecordingButton);
    }

    // ------------------------------------------------------------------ search / command palette

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = BuildSuggestions(sender.Text);
    }

    internal static List<SearchSuggestion> BuildSuggestions(string text)
    {
        var q = text.Trim();
        var list = new List<SearchSuggestion>();
        if (q.Length == 0) return list;
        var lower = q.ToLowerInvariant();
        var parsed = QueryParser.Parse(q, DateTimeOffset.Now);
        if (parsed.Intent == QueryIntent.Navigate && parsed.NavigateTarget is { } target && PageRegistry.Find(target) is { } nav)
            list.Add(new SearchSuggestion($"Show {nav.Title} — {parsed.Range.Label}", "Command", nav.Glyph, "nav", nav.Tag, parsed.RangeExplicit ? parsed.Range : null));
        foreach (var p in PageRegistry.All.Where(p => p.Group != "Hidden"))
        {
            var score = p.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 3
                : p.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2
                : p.Keywords.Any(k => k.Contains(lower, StringComparison.OrdinalIgnoreCase) || lower.Contains(k, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
            if (score > 0 && list.All(s => s.Target != p.Tag)) list.Add(new SearchSuggestion(p.Title, p.Description, p.Glyph, "page", p.Tag, null));
        }
        list.Add(new SearchSuggestion($"Ask Sentinel: \"{q}\"", "Answer from local telemetry", "", "ask", "Ask", q));
        return list.Take(8).ToList();
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchSuggestion s) Execute(s);
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchSuggestion chosen)
        {
            Execute(chosen);
            return;
        }
        var q = args.QueryText.Trim();
        var suggestions = BuildSuggestions(q);
        var target = suggestions.FirstOrDefault(s => s.Kind == "nav")
                     ?? suggestions.FirstOrDefault(s => s.Kind == "page" && s.Title.Equals(q, StringComparison.OrdinalIgnoreCase))
                     ?? suggestions.LastOrDefault();
        if (target is not null) Execute(target);
    }

    private void Execute(SearchSuggestion s)
    {
        SearchBox.Text = "";
        SearchBox.ItemsSource = null;
        NavigateTo(s.Target, s.Parameter);
    }
}
