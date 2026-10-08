using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Sentinel.App.Services;
using Windows.Foundation;

namespace Sentinel.App.Controls;

/// <summary>Shared motion helpers. Every effect is skipped when Windows animations, Reduce motion or Battery Saver say so.</summary>
public static class Motion
{
    public static bool Enabled => App.Services?.GetService<MotionPolicy>()?.AnimationsEnabled ?? true;

    /// <summary>
    /// Pointer-over lift for interactive surfaces: rises 2 px and takes an accent-tinted stroke, with eased transitions.
    /// </summary>
    public static void AddHoverLift(Border surface)
    {
        surface.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(150) };
        surface.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        object? resting = null;
        surface.PointerEntered += (_, _) =>
        {
            // Remember the local value (or "unset", meaning the style supplies it) so leaving restores it exactly.
            resting ??= surface.ReadLocalValue(Border.BorderBrushProperty);
            surface.BorderBrush = ThemeColors.ThemeBrush(surface, "BorderHoverBrush");
            if (Enabled) surface.Translation = new Vector3(0, -2, 0);
        };
        void Rest()
        {
            if (resting is null) return;
            if (resting == DependencyProperty.UnsetValue) surface.ClearValue(Border.BorderBrushProperty);
            else surface.SetValue(Border.BorderBrushProperty, resting);
            resting = null;
            surface.Translation = Vector3.Zero;
        }
        surface.PointerExited += (_, _) => Rest();
        surface.PointerCanceled += (_, _) => Rest();
        surface.PointerCaptureLost += (_, _) => Rest();
    }

    /// <summary>Staggered rise-and-fade for a page's sections as they load.</summary>
    public static void StaggerChildren(Panel panel)
    {
        if (!Enabled) return;
        panel.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition { IsStaggeringEnabled = true, FromVerticalOffset = 20 } };
    }

    internal static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];
}

/// <summary>
/// The standard page header: title, an optional subtitle and detail line, optional footer content (such as a link) and
/// right-aligned actions (such as a time-range selector) that sit on the subtitle's baseline. Narrow windows move the
/// actions below the text so nothing is squeezed.
/// </summary>
public sealed partial class PageHeader : UserControl
{
    private readonly Grid _root = new() { ColumnSpacing = 24, RowSpacing = 12, Margin = new Thickness(0, 28, 0, 0) };
    private readonly TextBlock _title = new() { Style = Motion.Res<Style>("PageTitleTextStyle"), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _subtitle = new() { Style = Motion.Res<Style>("PageSubtitleTextStyle"), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly TextBlock _detail = new() { Style = Motion.Res<Style>("LabelTextStyle"), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly ContentPresenter _footer = new() { Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ContentPresenter _actions = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
    private bool _stacked;

    public PageHeader()
    {
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);
        text.Children.Add(_detail);
        text.Children.Add(_footer);
        _root.Children.Add(text);
        Grid.SetColumn(_actions, 1);
        _root.Children.Add(_actions);
        Content = _root;
        AutomationProperties.SetHeadingLevel(_title, AutomationHeadingLevel.Level1);
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader),
        new PropertyMetadata("", (d, e) => ((PageHeader)d)._title.Text = (string)e.NewValue ?? ""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader),
        new PropertyMetadata(null, (d, e) => Show(((PageHeader)d)._subtitle, (string?)e.NewValue)));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(PageHeader),
        new PropertyMetadata(null, (d, e) => Show(((PageHeader)d)._detail, (string?)e.NewValue)));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader),
        new PropertyMetadata(null, (d, e) => ((PageHeader)d)._actions.Content = e.NewValue));
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(object), typeof(PageHeader),
        new PropertyMetadata(null, (d, e) => ((PageHeader)d)._footer.Content = e.NewValue));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? Detail { get => (string?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Arrange(double width)
    {
        var stack = width < 720;
        if (stack == _stacked) return;
        _stacked = stack;
        Grid.SetRow(_actions, stack ? 1 : 0);
        Grid.SetColumn(_actions, stack ? 0 : 1);
        _actions.HorizontalAlignment = stack ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }
}

/// <summary>A card's header row: optional accent glyph, title and subtitle on the left, actions vertically centred on the right.</summary>
public sealed partial class CardHeader : UserControl
{
    private readonly FontIcon _icon = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _title = new() { Style = Motion.Res<Style>("CardTitleTextStyle") };
    private readonly TextBlock _subtitle = new() { Style = Motion.Res<Style>("LabelTextStyle"), Visibility = Visibility.Collapsed };
    private readonly ContentPresenter _actions = new() { VerticalAlignment = VerticalAlignment.Center };

    public CardHeader()
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Loaded += (_, _) => _icon.Foreground = ThemeColors.ThemeBrush(this, "AccentGlyphBrush");
        ActualThemeChanged += (_, _) => _icon.Foreground = ThemeColors.ThemeBrush(this, "AccentGlyphBrush");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_actions, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(text);
        grid.Children.Add(_actions);
        Content = grid;
        AutomationProperties.SetHeadingLevel(_title, AutomationHeadingLevel.Level2);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(CardHeader),
        new PropertyMetadata("", (d, e) => ((CardHeader)d)._title.Text = (string)e.NewValue ?? ""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(CardHeader),
        new PropertyMetadata(null, (d, e) =>
        {
            var h = (CardHeader)d;
            h._subtitle.Text = (string?)e.NewValue ?? "";
            h._subtitle.Visibility = string.IsNullOrWhiteSpace((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible;
        }));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(CardHeader),
        new PropertyMetadata(null, (d, e) =>
        {
            var h = (CardHeader)d;
            h._icon.Glyph = (string?)e.NewValue ?? "";
            h._icon.Visibility = string.IsNullOrEmpty((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible;
        }));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(CardHeader),
        new PropertyMetadata(null, (d, e) => ((CardHeader)d)._actions.Content = e.NewValue));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
}

/// <summary>Flows children left to right and wraps them onto new lines (chips, tags, suggestion buttons).</summary>
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 8;

    /// <summary>Centre each line (for chip clouds under a centred heading).</summary>
    public bool CenterLines { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (s.Width == 0 && s.Height == 0) continue;
            if (x > 0 && x + s.Width > availableSize.Width)
            {
                y += line + VerticalSpacing;
                x = 0;
                line = 0;
            }
            x += s.Width + HorizontalSpacing;
            widest = Math.Max(widest, x - HorizontalSpacing);
            line = Math.Max(line, s.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? widest : Math.Min(widest, availableSize.Width), y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Break into lines first so each line can be centred as a whole.
        var lines = new List<(List<UIElement> Items, double Width, double Height)>();
        var current = new List<UIElement>();
        double x = 0, height = 0;
        foreach (var child in Children)
        {
            var s = child.DesiredSize;
            if (s.Width == 0 && s.Height == 0) continue;
            if (current.Count > 0 && x + s.Width > finalSize.Width)
            {
                lines.Add((current, x - HorizontalSpacing, height));
                current = [];
                x = 0;
                height = 0;
            }
            current.Add(child);
            x += s.Width + HorizontalSpacing;
            height = Math.Max(height, s.Height);
        }
        if (current.Count > 0) lines.Add((current, x - HorizontalSpacing, height));

        double y = 0;
        foreach (var (items, width, lineHeight) in lines)
        {
            var left = CenterLines ? Math.Max(0, (finalSize.Width - width) / 2) : 0;
            foreach (var child in items)
            {
                var s = child.DesiredSize;
                child.Arrange(new Rect(left, y, Math.Min(s.Width, finalSize.Width), s.Height));
                left += s.Width + HorizontalSpacing;
            }
            y += lineHeight + VerticalSpacing;
        }
        return finalSize;
    }
}
