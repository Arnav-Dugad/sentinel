using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sentinel.App.Controls;

/// <summary>Bindable compact chart for tiles.</summary>
public sealed partial class Sparkline : UserControl
{
    private readonly SentinelChart _chart = new() { Compact = true, ShowAxes = false, MinHeight = 36 };

    public Sparkline()
    {
        Content = _chart;
        IsHitTestVisible = false;
    }

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(Sparkline),
        new PropertyMetadata(null, (d, e) => ((Sparkline)d).Update()));
    public static readonly DependencyProperty ColorIndexProperty = DependencyProperty.Register(nameof(ColorIndex), typeof(int), typeof(Sparkline),
        new PropertyMetadata(1, (d, e) => ((Sparkline)d).Update()));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(double), typeof(Sparkline),
        new PropertyMetadata(double.NaN, (d, e) => ((Sparkline)d).Update()));

    public IReadOnlyList<ChartPoint>? Points { get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public int ColorIndex { get => (int)GetValue(ColorIndexProperty); set => SetValue(ColorIndexProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }

    private void Update()
    {
        var pts = Points ?? [];
        _chart.Series.Clear();
        _chart.Series.Add(new ChartSeries { Name = "", Fill = true, ColorIndex = ColorIndex, Points = pts });
        _chart.YMin = 0;
        _chart.YMax = double.IsNaN(Max) ? null : Max;
        if (pts.Count > 0) _chart.SetRange(pts[0].T, pts[^1].T);
        _chart.Redraw();
    }
}
