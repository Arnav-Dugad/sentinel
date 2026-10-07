using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Sentinel.App.Controls;

public readonly record struct ChartPoint(DateTimeOffset T, double V);

public sealed class ChartSeries
{
    public required string Name { get; init; }
    public int ColorIndex { get; init; } = 1;
    public IReadOnlyList<ChartPoint> Points { get; set; } = [];
    public Func<double, string> Format { get; init; } = v => v.ToString("F1", CultureInfo.CurrentCulture);
    public bool Fill { get; init; }
    public bool Dashed { get; init; }
    /// <summary>Plotted on its own scale (normalised) so series with different units can be overlaid.</summary>
    public bool OwnScale { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
}

public sealed record ChartMarker(DateTimeOffset T, string Label);

public sealed record ChartBand(DateTimeOffset From, DateTimeOffset To, string Label);

/// <summary>
/// Sentinel's chart: lines/areas over time with gaps (never interpolating across missing data), min/max decimation
/// to pixel width, multi-series overlays, event markers, anomaly bands, a hover cursor with exact values, and
/// optional wheel-zoom / drag-pan. It redraws only when data or size changes — never per frame.
/// </summary>
public sealed partial class SentinelChart : Grid
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Canvas _overlay = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Line _cursor = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly Border _tip = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), IsHitTestVisible = false };
    private readonly StackPanel _tipPanel = new() { Spacing = 2 };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 6, 0, 0) };
    private string _legendKey = "";
    private Rect _plot;
    private Point? _dragStart;
    private (DateTimeOffset From, DateTimeOffset To) _dragRange;

    public SentinelChart()
    {
        MinHeight = 80;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_canvas);
        Children.Add(_overlay);
        SetRow(_legend, 1);
        Children.Add(_legend);
        _overlay.Children.Add(_cursor);
        _tip.Child = _tipPanel;
        _overlay.Children.Add(_tip);
        _overlay.SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        _overlay.PointerMoved += OnPointerMoved;
        _overlay.PointerExited += (_, _) => HideCursor();
        _overlay.PointerWheelChanged += OnWheel;
        _overlay.PointerPressed += OnPressed;
        _overlay.PointerReleased += (_, e) =>
        {
            _dragStart = null;
            _overlay.ReleasePointerCapture(e.Pointer);
        };
        AutomationProperties.SetName(this, "Chart");
    }

    internal IList<ChartSeries> Series { get; } = new List<ChartSeries>();
    internal IList<ChartMarker> Markers { get; } = new List<ChartMarker>();
    internal IList<ChartBand> Bands { get; } = new List<ChartBand>();
    public DateTimeOffset From { get; set; } = DateTimeOffset.Now.AddMinutes(-10);
    public DateTimeOffset To { get; set; } = DateTimeOffset.Now;
    public double? YMin { get; set; }
    public double? YMax { get; set; }
    public bool ShowAxes { get; set; } = true;
    public bool AllowZoom { get; set; }
    public bool Compact { get; set; }

    /// <summary>Raised after the user zooms or pans, so the host can load data for the new range.</summary>
    public event EventHandler<(DateTimeOffset From, DateTimeOffset To)>? ViewportChanged;

    public void SetRange(DateTimeOffset from, DateTimeOffset to)
    {
        From = from;
        To = to > from ? to : from.AddMinutes(1);
    }

    public void Redraw()
    {
        _canvas.Children.Clear();
        UpdateLegend();
        var w = _overlay.ActualWidth;
        var h = _overlay.ActualHeight;
        if (w < 20 || h < 20) return;

        var (pMin, pMax) = PrimaryRange();
        var primary = Series.FirstOrDefault(s => !s.OwnScale);
        var axisLabels = Enumerable.Range(0, 5).Select(i => primary?.Format(pMax - (pMax - pMin) * i / 4) ?? "").ToList();

        // The Y axis is as wide as its widest label, so units such as "2.7 MiB/s" are never clipped.
        var left = 0.0;
        if (ShowAxes && !Compact && primary is not null)
        {
            foreach (var label in axisLabels)
            {
                var probe = new TextBlock { Text = label, FontSize = 11 };
                probe.Measure(new Size(400, 40));
                left = Math.Max(left, probe.DesiredSize.Width);
            }
            left = Math.Max(28, left + 10);
        }
        var bottom = ShowAxes && !Compact ? 22.0 : 0;
        var top = Markers.Count > 0 && !Compact ? 14.0 : 2;
        _plot = new Rect(left, top, Math.Max(10, w - left - 4), Math.Max(10, h - top - bottom));

        var grid = ThemeColors.Brush(this, "ChartGridColor");
        var axisText = ThemeColors.Brush(this, "ChartAxisTextColor");
        _cursor.Stroke = axisText;
        _tip.Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"];
        _tip.BorderBrush = ThemeColors.Brush(this, "ChartGridColor");
        _tip.BorderThickness = new Thickness(1);

        if (!Compact)
        {
            for (var i = 0; i <= 4; i++)
            {
                var y = _plot.Top + _plot.Height * i / 4;
                _canvas.Children.Add(new Line { X1 = _plot.Left, X2 = _plot.Right, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
                if (ShowAxes && primary is not null)
                {
                    var t = new TextBlock { Text = axisLabels[i], FontSize = 11, Foreground = axisText, Width = left - 6, TextAlignment = TextAlignment.Right };
                    Canvas.SetLeft(t, 0);
                    Canvas.SetTop(t, y - 8);
                    _canvas.Children.Add(t);
                }
            }
            if (ShowAxes) DrawTimeAxis(axisText);
        }

        var band = ThemeColors.Brush(this, "ChartBandColor");
        foreach (var b in Bands)
        {
            var x1 = Math.Max(_plot.Left, X(b.From));
            var x2 = Math.Min(_plot.Right, X(b.To));
            if (x2 <= x1) continue;
            var r = new Rectangle { Width = Math.Max(2, x2 - x1), Height = _plot.Height, Fill = band };
            Canvas.SetLeft(r, x1);
            Canvas.SetTop(r, _plot.Top);
            _canvas.Children.Add(r);
        }

        foreach (var s in Series)
        {
            var (min, max) = s.OwnScale ? OwnRange(s) : (pMin, pMax);
            DrawSeries(s, min, max);
        }

        foreach (var m in Markers)
        {
            var x = X(m.T);
            if (x < _plot.Left || x > _plot.Right) continue;
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = _plot.Top, Y2 = _plot.Bottom, Stroke = axisText, StrokeThickness = 1, Opacity = 0.35, StrokeDashArray = [2, 3] });
            var tri = new Polygon
            {
                Points = { new Point(x - 4, 2), new Point(x + 4, 2), new Point(x, 9) },
                Fill = ThemeColors.Brush(this, "StatusInfoColor"),
            };
            _canvas.Children.Add(tri);
        }

        AutomationProperties.SetHelpText(this, Describe());
    }

    /// <summary>Series names with their colours, so overlays are readable without hovering. Rebuilt only when series change.</summary>
    private void UpdateLegend()
    {
        var show = !Compact && Series.Count > 1;
        var key = show ? string.Join("|", Series.Select(s => s.Name + s.ColorIndex + s.Dashed)) + ActualTheme : "";
        if (key == _legendKey) return;
        _legendKey = key;
        _legend.Children.Clear();
        _legend.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        foreach (var s in Series)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var swatch = new Line { X1 = 0, X2 = 14, Y1 = 6, Y2 = 6, StrokeThickness = 2.5, Stroke = ThemeColors.Series(this, s.ColorIndex), VerticalAlignment = VerticalAlignment.Center };
            if (s.Dashed) swatch.StrokeDashArray = [2, 1.5];
            item.Children.Add(swatch);
            item.Children.Add(new TextBlock { Text = s.Name, FontSize = 12, Foreground = ThemeColors.Brush(this, "ChartAxisTextColor") });
            _legend.Children.Add(item);
        }
    }

    private string Describe()
    {
        var parts = new List<string>();
        foreach (var s in Series)
        {
            var vals = s.Points.Where(p => !double.IsNaN(p.V) && p.T >= From && p.T <= To).Select(p => p.V).ToList();
            if (vals.Count == 0) continue;
            parts.Add($"{s.Name}: latest {s.Format(vals[^1])}, minimum {s.Format(vals.Min())}, maximum {s.Format(vals.Max())}");
        }
        return parts.Count == 0 ? "No data in this range." : string.Join(". ", parts) + ".";
    }

    private void DrawTimeAxis(Brush brush)
    {
        var span = To - From;
        var fmt = span <= TimeSpan.FromMinutes(15) ? "T" : span <= TimeSpan.FromDays(2) ? "t" : "MMM d";
        var ticks = Math.Clamp((int)(_plot.Width / 110), 2, 7);
        for (var i = 0; i <= ticks; i++)
        {
            var t = From + span * i / ticks;
            var x = X(t);
            var tb = new TextBlock { Text = t.ToLocalTime().ToString(fmt, CultureInfo.CurrentCulture), FontSize = 11, Foreground = brush };
            tb.Measure(new Size(200, 40));
            var lx = Math.Clamp(x - tb.DesiredSize.Width / 2, _plot.Left, Math.Max(_plot.Left, _plot.Right - tb.DesiredSize.Width));
            Canvas.SetLeft(tb, lx);
            Canvas.SetTop(tb, _plot.Bottom + 4);
            _canvas.Children.Add(tb);
        }
    }

    private (double Min, double Max) PrimaryRange()
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in Series.Where(s => !s.OwnScale))
            foreach (var p in s.Points)
            {
                if (double.IsNaN(p.V) || p.T < From || p.T > To) continue;
                min = Math.Min(min, p.V);
                max = Math.Max(max, p.V);
            }
        if (min == double.MaxValue) (min, max) = (0, 1);
        if (YMin is { } fmin) min = fmin;
        else min = min >= 0 && min - (max - min) * 0.1 < 0 ? 0 : min - (max - min) * 0.1;
        if (YMax is { } fmax) max = Math.Max(fmax, max);
        else max += (max - min) * 0.12;
        if (max - min < 1e-9) max = min + 1;
        return (min, max);
    }

    private (double Min, double Max) OwnRange(ChartSeries s)
    {
        var vals = s.Points.Where(p => !double.IsNaN(p.V) && p.T >= From && p.T <= To).Select(p => p.V).ToList();
        double min = s.Min ?? (vals.Count > 0 ? vals.Min() : 0);
        double max = s.Max ?? (vals.Count > 0 ? vals.Max() : 1);
        var pad = (max - min) * 0.1;
        if (s.Min is null) min -= pad;
        if (s.Max is null) max += pad;
        if (max - min < 1e-9) max = min + 1;
        return (min, max);
    }

    private double X(DateTimeOffset t) => _plot.Left + (t - From).TotalMilliseconds / Math.Max(1, (To - From).TotalMilliseconds) * _plot.Width;

    private double Y(double v, double min, double max) => _plot.Bottom - (v - min) / (max - min) * _plot.Height;

    private void DrawSeries(ChartSeries s, double min, double max)
    {
        var pts = Decimate(s.Points.Where(p => p.T >= From.AddSeconds(-30) && p.T <= To.AddSeconds(30)).ToList());
        if (pts.Count == 0) return;
        var gap = GapThreshold(pts);
        var stroke = ThemeColors.Series(this, s.ColorIndex);
        var lineGeometry = new PathGeometry();
        var fillGeometry = new PathGeometry();
        PathFigure? line = null, fill = null;
        Point last = default;
        DateTimeOffset? lastT = null;
        foreach (var p in pts)
        {
            if (double.IsNaN(p.V) || (lastT is { } lt && p.T - lt > gap))
            {
                Close(fill, last);
                line = fill = null;
                lastT = double.IsNaN(p.V) ? null : lastT;
                if (double.IsNaN(p.V)) continue;
            }
            var pt = new Point(Math.Clamp(X(p.T), _plot.Left, _plot.Right), Math.Clamp(Y(p.V, min, max), _plot.Top, _plot.Bottom));
            if (line is null)
            {
                line = new PathFigure { StartPoint = pt };
                lineGeometry.Figures.Add(line);
                if (s.Fill)
                {
                    fill = new PathFigure { StartPoint = new Point(pt.X, _plot.Bottom), IsClosed = true };
                    fill.Segments.Add(new LineSegment { Point = pt });
                    fillGeometry.Figures.Add(fill);
                }
            }
            else
            {
                line.Segments.Add(new LineSegment { Point = pt });
                fill?.Segments.Add(new LineSegment { Point = pt });
            }
            last = pt;
            lastT = p.T;
        }
        Close(fill, last);

        if (s.Fill)
        {
            var fb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fb.GradientStops.Add(new GradientStop { Color = WithAlpha(stroke.Color, 0x40), Offset = 0 });
            fb.GradientStops.Add(new GradientStop { Color = WithAlpha(stroke.Color, 0x04), Offset = 1 });
            _canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = fillGeometry, Fill = fb });
        }
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = lineGeometry,
            Stroke = stroke,
            StrokeThickness = Compact ? 1.5 : 1.75,
            StrokeLineJoin = PenLineJoin.Round,
        };
        if (s.Dashed) path.StrokeDashArray = [4, 3];
        _canvas.Children.Add(path);

        void Close(PathFigure? f, Point l)
        {
            if (f is null) return;
            f.Segments.Add(new LineSegment { Point = new Point(l.X, _plot.Bottom) });
        }
    }

    private static global::Windows.UI.Color WithAlpha(global::Windows.UI.Color c, byte a) => global::Windows.UI.Color.FromArgb(a, c.R, c.G, c.B);

    private static TimeSpan GapThreshold(List<ChartPoint> pts)
    {
        if (pts.Count < 3) return TimeSpan.FromMinutes(5);
        var deltas = new List<double>(Math.Min(pts.Count, 200));
        for (var i = 1; i < pts.Count && deltas.Count < 200; i++) deltas.Add((pts[i].T - pts[i - 1].T).TotalSeconds);
        deltas.Sort();
        var median = deltas[deltas.Count / 2];
        return TimeSpan.FromSeconds(Math.Max(median * 4, 15));
    }

    /// <summary>Keeps the min and max of each pixel column so spikes survive decimation.</summary>
    private List<ChartPoint> Decimate(List<ChartPoint> pts)
    {
        var buckets = (int)Math.Max(10, _plot.Width);
        if (pts.Count <= buckets * 2) return pts;
        var result = new List<ChartPoint>(buckets * 2 + 2);
        var span = Math.Max(1, (To - From).TotalMilliseconds);
        var i = 0;
        while (i < pts.Count)
        {
            var bucket = (int)((pts[i].T - From).TotalMilliseconds / span * buckets);
            ChartPoint? lo = null, hi = null;
            var hasNaN = false;
            while (i < pts.Count && (int)((pts[i].T - From).TotalMilliseconds / span * buckets) == bucket)
            {
                var p = pts[i++];
                if (double.IsNaN(p.V))
                {
                    hasNaN = true;
                    continue;
                }
                if (lo is null || p.V < lo.Value.V) lo = p;
                if (hi is null || p.V > hi.Value.V) hi = p;
            }
            if (lo is { } l && hi is { } h)
            {
                if (l.T <= h.T)
                {
                    result.Add(l);
                    if (h != l) result.Add(h);
                }
                else
                {
                    result.Add(h);
                    result.Add(l);
                }
            }
            if (hasNaN) result.Add(new ChartPoint(lo?.T ?? From, double.NaN));
        }
        return result;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_overlay).Position;
        if (_dragStart is { } start && AllowZoom)
        {
            var dx = pos.X - start.X;
            var span = _dragRange.To - _dragRange.From;
            var shift = TimeSpan.FromMilliseconds(-dx / _plot.Width * span.TotalMilliseconds);
            var to = _dragRange.To + shift;
            if (to > DateTimeOffset.Now) shift -= to - DateTimeOffset.Now;
            SetRange(_dragRange.From + shift, _dragRange.To + shift);
            Redraw();
            return;
        }
        if (Compact || pos.X < _plot.Left || pos.X > _plot.Right || Series.Count == 0)
        {
            HideCursor();
            return;
        }
        var t = From + TimeSpan.FromMilliseconds((pos.X - _plot.Left) / _plot.Width * (To - From).TotalMilliseconds);
        _cursor.X1 = _cursor.X2 = pos.X;
        _cursor.Y1 = _plot.Top;
        _cursor.Y2 = _plot.Bottom;
        _cursor.Visibility = Visibility.Visible;

        _tipPanel.Children.Clear();
        _tipPanel.Children.Add(new TextBlock { Text = t.ToLocalTime().ToString((To - From) > TimeSpan.FromDays(2) ? "g" : "T", CultureInfo.CurrentCulture), FontSize = 12, Opacity = 0.75 });
        foreach (var s in Series)
        {
            var nearest = Nearest(s.Points, t);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = ThemeColors.Series(this, s.ColorIndex), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{s.Name}: {(nearest is { } n ? s.Format(n.V) : "no data")}", FontSize = 12 });
            _tipPanel.Children.Add(row);
        }
        foreach (var m in Markers.Where(m => Math.Abs((m.T - t).TotalMilliseconds) < (To - From).TotalMilliseconds / 60))
            _tipPanel.Children.Add(new TextBlock { Text = "▲ " + m.Label, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 });
        foreach (var b in Bands.Where(b => t >= b.From && t <= b.To))
            _tipPanel.Children.Add(new TextBlock { Text = "◆ " + b.Label, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 });

        _tip.Visibility = Visibility.Visible;
        _tip.Measure(new Size(400, 400));
        var tx = pos.X + 14 + _tip.DesiredSize.Width > _overlay.ActualWidth ? pos.X - 14 - _tip.DesiredSize.Width : pos.X + 14;
        Canvas.SetLeft(_tip, Math.Max(0, tx));
        Canvas.SetTop(_tip, Math.Clamp(pos.Y - _tip.DesiredSize.Height / 2, 0, Math.Max(0, _overlay.ActualHeight - _tip.DesiredSize.Height)));
    }

    private ChartPoint? Nearest(IReadOnlyList<ChartPoint> points, DateTimeOffset t)
    {
        if (points.Count == 0) return null;
        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].T < t) lo = mid + 1;
            else hi = mid;
        }
        var best = lo;
        if (lo > 0 && (t - points[lo - 1].T).Duration() < (points[lo].T - t).Duration()) best = lo - 1;
        var p = points[best];
        var tolerance = TimeSpan.FromMilliseconds(Math.Max((To - From).TotalMilliseconds / 40, 3000));
        return double.IsNaN(p.V) || (p.T - t).Duration() > tolerance ? null : p;
    }

    private void HideCursor()
    {
        _cursor.Visibility = Visibility.Collapsed;
        _tip.Visibility = Visibility.Collapsed;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!AllowZoom) return;
        var p = e.GetCurrentPoint(_overlay);
        var delta = p.Properties.MouseWheelDelta;
        var factor = delta > 0 ? 0.8 : 1.25;
        var anchor = From + TimeSpan.FromMilliseconds(Math.Clamp((p.Position.X - _plot.Left) / _plot.Width, 0, 1) * (To - From).TotalMilliseconds);
        var newSpan = TimeSpan.FromMilliseconds(Math.Clamp((To - From).TotalMilliseconds * factor, 60_000, TimeSpan.FromDays(400).TotalMilliseconds));
        var ratio = (anchor - From).TotalMilliseconds / Math.Max(1, (To - From).TotalMilliseconds);
        var from = anchor - newSpan * ratio;
        var to = from + newSpan;
        if (to > DateTimeOffset.Now)
        {
            from -= to - DateTimeOffset.Now;
            to = DateTimeOffset.Now;
        }
        SetRange(from, to);
        Redraw();
        ViewportChanged?.Invoke(this, (From, To));
        e.Handled = true;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!AllowZoom || e.Pointer.PointerDeviceType == PointerDeviceType.Pen) return;
        _dragStart = e.GetCurrentPoint(_overlay).Position;
        _dragRange = (From, To);
        _overlay.CapturePointer(e.Pointer);
        HideCursor();
        _overlay.PointerReleased -= RaiseAfterDrag;
        _overlay.PointerReleased += RaiseAfterDrag;
    }

    private void RaiseAfterDrag(object sender, PointerRoutedEventArgs e)
    {
        _overlay.PointerReleased -= RaiseAfterDrag;
        if (_dragRange.From != From) ViewportChanged?.Invoke(this, (From, To));
    }
}
