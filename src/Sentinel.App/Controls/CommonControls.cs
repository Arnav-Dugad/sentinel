using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sentinel.App.Services;
using Sentinel.Domain;

namespace Sentinel.App.Controls;

/// <summary>
/// A live metric tile: accent icon badge and label, a large single-line value and a caption of up to two lines (full
/// text in the tooltip). With <see cref="Chrome"/> it draws its own card surface and lifts on hover; without it, it
/// sits flat inside a larger card. <see cref="Footer"/> hosts extras such as a progress bar.
/// </summary>
public sealed partial class MetricTile : UserControl
{
    private readonly Border _surface = new();
    private readonly Border _badge = new() { Width = 28, Height = 28, CornerRadius = new CornerRadius(6) };
    private readonly FontIcon _icon = new() { FontSize = 14 };
    private readonly TextBlock _label = new()
    {
        FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly TextBlock _value = new() { Style = (Style)Application.Current.Resources["MetricValueTextStyle"], Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _caption = new()
    {
        Style = (Style)Application.Current.Resources["LabelTextStyle"], Margin = new Thickness(0, 4, 0, 0), MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly ContentPresenter _footer = new() { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private bool _hoverWired;

    public MetricTile()
    {
        _badge.Child = _icon;
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_label, 1);
        header.Children.Add(_badge);
        header.Children.Add(_label);
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_value);
        stack.Children.Add(_caption);
        stack.Children.Add(_footer);
        _surface.Child = stack;
        Content = _surface;
        Loaded += (_, _) => ApplyTheme();
        ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyChrome();
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(MetricTile),
        new PropertyMetadata("", (d, e) => ((MetricTile)d).Update()));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricTile),
        new PropertyMetadata("—", (d, e) => ((MetricTile)d).Update()));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(MetricTile),
        new PropertyMetadata("", (d, e) => ((MetricTile)d).Update()));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(MetricTile),
        new PropertyMetadata("", (d, e) => ((MetricTile)d).Update()));
    public static readonly DependencyProperty ProvenanceProperty = DependencyProperty.Register(nameof(Provenance), typeof(string), typeof(MetricTile),
        new PropertyMetadata(null, (d, e) => ((MetricTile)d).Update()));
    public static readonly DependencyProperty ChromeProperty = DependencyProperty.Register(nameof(Chrome), typeof(bool), typeof(MetricTile),
        new PropertyMetadata(true, (d, e) => ((MetricTile)d).ApplyChrome()));
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(object), typeof(MetricTile),
        new PropertyMetadata(null, (d, e) =>
        {
            var t = (MetricTile)d;
            t._footer.Content = e.NewValue;
            t._footer.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
        }));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Provenance { get => (string?)GetValue(ProvenanceProperty); set => SetValue(ProvenanceProperty, value); }
    public bool Chrome { get => (bool)GetValue(ChromeProperty); set => SetValue(ChromeProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    private void ApplyChrome()
    {
        if (Chrome)
        {
            _surface.Style = (Style)Application.Current.Resources["CompactCardStyle"];
            _surface.MinHeight = 120;
            if (!_hoverWired)
            {
                _hoverWired = true;
                Motion.AddHoverLift(_surface);
            }
        }
        else
        {
            _surface.ClearValue(StyleProperty);
            _surface.Padding = new Thickness(0);
            _surface.MinHeight = 0;
        }
    }

    private void ApplyTheme()
    {
        _badge.Background = ThemeColors.ThemeBrush(this, "AccentSubtleBrush");
        _icon.Foreground = ThemeColors.ThemeBrush(this, "AccentGlyphBrush");
        _label.Foreground = ThemeColors.ThemeBrush(this, "TextSecondaryBrush");
    }

    private void Update()
    {
        _label.Text = Label;
        _value.Text = string.IsNullOrEmpty(Value) ? "—" : Value;
        _caption.Text = Caption ?? "";
        _caption.Visibility = string.IsNullOrEmpty(Caption) ? Visibility.Collapsed : Visibility.Visible;
        _icon.Glyph = Glyph ?? "";
        _badge.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        var parts = new[] { string.IsNullOrEmpty(Caption) ? null : Caption, string.IsNullOrEmpty(Provenance) ? null : "Source: " + Provenance };
        var tip = string.Join("\n", parts.Where(t => t is not null));
        ToolTipService.SetToolTip(this, tip.Length == 0 ? null : tip);
        AutomationProperties.SetName(this, $"{Label}: {_value.Text}. {Caption}");
    }
}

/// <summary>Label/value rows. Sensitive values are masked unless the user chooses to reveal them; each row can carry its source.</summary>
public sealed partial class InfoList : UserControl
{
    private readonly Grid _grid = new() { ColumnSpacing = 32, RowSpacing = 12 };
    private SensitiveInfoState? _sensitive;

    public InfoList()
    {
        // The label column takes the width of the longest label (capped), so short labels never wrap and values align.
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 120, MaxWidth = 280 });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = _grid;
        Loaded += (_, _) =>
        {
            _sensitive = App.Services.GetService<SensitiveInfoState>();
            if (_sensitive is not null) _sensitive.PropertyChanged += OnSensitiveChanged;
            Render();
        };
        Unloaded += (_, _) =>
        {
            if (_sensitive is not null) _sensitive.PropertyChanged -= OnSensitiveChanged;
        };
    }

    private void OnSensitiveChanged(object? sender, PropertyChangedEventArgs e) => Render();

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<InfoItem>), typeof(InfoList),
        new PropertyMetadata(null, (d, e) => ((InfoList)d).Render()));

    public IReadOnlyList<InfoItem>? Items { get => (IReadOnlyList<InfoItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    private void Render()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        if (Items is null) return;
        var show = _sensitive?.ShowSensitive == true;
        var row = 0;
        foreach (var item in Items)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = item.Label, Style = (Style)Application.Current.Resources["MutedTextStyle"] };
            var value = item.Value is null
                ? new TextBlock { Text = "Not exposed by this system", Style = (Style)Application.Current.Resources["MutedTextStyle"], FontStyle = global::Windows.UI.Text.FontStyle.Italic }
                : new TextBlock
                {
                    Text = item.Sensitive && !show ? Core.Privacy.Redactor.Mask(item.Value) : item.Value,
                    Style = (Style)Application.Current.Resources["BodyTextStyle"],
                    IsTextSelectionEnabled = !item.Sensitive || show,
                };
            var tip = string.Join("\n", new[] { item.Tooltip, item.Source is null ? null : "Source: " + item.Source }.Where(s => s is not null));
            if (tip.Length > 0) ToolTipService.SetToolTip(value, tip);
            AutomationProperties.SetName(value, $"{item.Label}: {(item.Sensitive && !show ? "hidden" : item.Value ?? "not exposed")}");
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            _grid.Children.Add(label);
            _grid.Children.Add(value);
            row++;
        }
    }
}

/// <summary>Calm explanation shown instead of an error when information is not available: an accent badge, a title and a short message.</summary>
public sealed partial class EmptyState : UserControl
{
    private readonly Border _badge = new() { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly FontIcon _icon = new() { FontSize = 22, Glyph = "\uE946" };
    private readonly TextBlock _title = new()
    {
        FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 14, 0, 4), HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly TextBlock _message = new()
    {
        Style = (Style)Application.Current.Resources["MutedTextStyle"], TextAlignment = TextAlignment.Center, MaxWidth = 460,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public EmptyState()
    {
        _badge.Child = _icon;
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(16, 24, 16, 24) };
        s.Children.Add(_badge);
        s.Children.Add(_title);
        s.Children.Add(_message);
        Content = s;
        Loaded += (_, _) => ApplyTheme();
        ActualThemeChanged += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        _badge.Background = ThemeColors.ThemeBrush(this, "AccentSubtleBrush");
        _icon.Foreground = ThemeColors.ThemeBrush(this, "AccentGlyphBrush");
        _title.Foreground = ThemeColors.ThemeBrush(this, "TextPrimaryBrush");
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) =>
        {
            var x = (EmptyState)d;
            x._title.Text = (string?)e.NewValue ?? "";
            x._title.Visibility = string.IsNullOrEmpty((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible;
        }));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) => ((EmptyState)d)._message.Text = (string)e.NewValue ?? ""));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) =>
        {
            if (!string.IsNullOrEmpty((string)e.NewValue)) ((EmptyState)d)._icon.Glyph = (string)e.NewValue;
        }));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
}

public sealed record EvidenceRow(EvidenceKind Kind, string Text, string? Source);

/// <summary>
/// Shows statements grouped as Observed / Inferred / Possible / Unknown so interpretation is never confused with telemetry.
/// </summary>
public sealed partial class EvidenceList : UserControl
{
    private readonly StackPanel _panel = new() { Spacing = 10 };

    public EvidenceList()
    {
        Content = _panel;
        ActualThemeChanged += (_, _) => Render();
    }

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<EvidenceRow>), typeof(EvidenceList),
        new PropertyMetadata(null, (d, e) => ((EvidenceList)d).Render()));

    public IReadOnlyList<EvidenceRow>? Items { get => (IReadOnlyList<EvidenceRow>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    private void Render()
    {
        _panel.Children.Clear();
        if (Items is null) return;
        foreach (var group in Items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
        {
            var (glyph, color) = group.Key switch
            {
                EvidenceKind.Observed => ("", "StatusInfoColor"),
                EvidenceKind.Inferred => ("", "StatusHealthyColor"),
                EvidenceKind.Possible => ("", "StatusAttentionColor"),
                _ => ("", "StatusUnknownColor"),
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = ThemeColors.Brush(this, color) });
            header.Children.Add(new TextBlock { Text = group.Key.Label(), FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = ThemeColors.Brush(this, color) });
            var groupPanel = new StackPanel { Spacing = 6 };
            groupPanel.Children.Add(header);
            foreach (var item in group)
            {
                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(19, 0, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var text = new TextBlock { Text = item.Text, Style = (Style)Application.Current.Resources["BodyTextStyle"], IsTextSelectionEnabled = true };
                if (item.Source is not null) ToolTipService.SetToolTip(text, "Source: " + item.Source);
                row.Children.Add(text);
                AutomationProperties.SetName(text, $"{group.Key.Label()}: {item.Text}");
                groupPanel.Children.Add(row);
            }
            _panel.Children.Add(groupPanel);
        }
    }
}

/// <summary>Status indicator that always pairs colour with an icon and text.</summary>
public sealed partial class StatusPill : UserControl
{
    private readonly Border _border = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 12, 4), BorderThickness = new Thickness(1) };
    private readonly FontIcon _icon = new() { FontSize = 12 };
    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };

    public StatusPill()
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        s.Children.Add(_icon);
        s.Children.Add(_text);
        _border.Child = s;
        Content = _border;
        ActualThemeChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
    }

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(HealthStatus), typeof(StatusPill),
        new PropertyMetadata(HealthStatus.Unknown, (d, e) => ((StatusPill)d).Update()));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusPill),
        new PropertyMetadata(null, (d, e) => ((StatusPill)d).Update()));

    public HealthStatus Status { get => (HealthStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static string GlyphFor(HealthStatus s) => s switch
    {
        HealthStatus.Critical => "",
        HealthStatus.Attention => "",
        HealthStatus.Unknown => "",
        HealthStatus.Normal => "",
        _ => "",
    };

    private void Update()
    {
        var brush = ThemeColors.Status(this, Status);
        _icon.Glyph = GlyphFor(Status);
        _icon.Foreground = brush;
        _text.Foreground = brush;
        _text.Text = Text ?? Status.Label();
        _border.BorderBrush = brush;
        _border.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(24, brush.Color.R, brush.Color.G, brush.Color.B));
        AutomationProperties.SetName(this, _text.Text);
    }
}

/// <summary>Universal time selector (Live, 1h, 6h, 24h, 7d, 30d) used on historical pages.</summary>
public sealed partial class RangeSelector : UserControl
{
    private readonly SelectorBar _bar = new();

    public RangeSelector()
    {
        foreach (var (key, _) in Intelligence.TimeRange.Presets)
            _bar.Items.Add(new SelectorBarItem { Text = key, Tag = key });
        _bar.SelectedItem = _bar.Items[0];
        _bar.SelectionChanged += (_, _) =>
        {
            if (_bar.SelectedItem?.Tag is string k && k != SelectedKey)
            {
                SelectedKey = k;
                Changed?.Invoke(this, k);
            }
        };
        Content = _bar;
        AutomationProperties.SetName(_bar, "Time range");
    }

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(string), typeof(RangeSelector),
        new PropertyMetadata("Live", (d, e) => ((RangeSelector)d).Sync()));

    public string SelectedKey { get => (string)GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }

    public event EventHandler<string>? Changed;

    private void Sync()
    {
        var item = _bar.Items.FirstOrDefault(i => (string)i.Tag == SelectedKey);
        if (item is not null && _bar.SelectedItem != item) _bar.SelectedItem = item;
    }

    public static TimeSpan SpanOf(string key) => Intelligence.TimeRange.Presets.FirstOrDefault(p => p.Key == key).Span is var s && s > TimeSpan.Zero ? s : TimeSpan.FromMinutes(10);
}
