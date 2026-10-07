using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Sentinel.App.Controls;

/// <summary>
/// Lays children out in as many equal-width columns as fit (each at least <see cref="MinItemWidth"/>), wrapping into rows.
/// Rows take the height of their tallest child, so cards in a row line up.
/// </summary>
public sealed partial class AdaptiveGrid : Panel
{
    public double MinItemWidth { get; set; } = 200;
    public double ColumnSpacing { get; set; } = 12;
    public double RowSpacing { get; set; } = 12;
    public int MaxColumns { get; set; } = 6;

    private int Columns(double width)
    {
        var visible = Children.Count(c => c.Visibility == Visibility.Visible);
        if (double.IsInfinity(width) || width <= 0) return Math.Max(1, Math.Min(visible, MaxColumns));
        var n = (int)Math.Floor((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing));
        return Math.Clamp(n, 1, Math.Max(1, Math.Min(MaxColumns, visible)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = Columns(availableSize.Width);
        var itemWidth = double.IsInfinity(availableSize.Width) ? MinItemWidth : (availableSize.Width - ColumnSpacing * (cols - 1)) / cols;
        double height = 0, rowHeight = 0;
        var i = 0;
        foreach (var child in Children.Where(c => c.Visibility == Visibility.Visible))
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++i % cols == 0)
            {
                height += rowHeight + RowSpacing;
                rowHeight = 0;
            }
        }
        height += rowHeight;
        if (i % cols == 0 && i > 0) height -= RowSpacing;
        return new Size(double.IsInfinity(availableSize.Width) ? itemWidth * cols : availableSize.Width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        var cols = Columns(finalSize.Width);
        var itemWidth = (finalSize.Width - ColumnSpacing * (cols - 1)) / cols;
        double y = 0;
        for (var r = 0; r * cols < visible.Count; r++)
        {
            var row = visible.Skip(r * cols).Take(cols).ToList();
            var h = row.Max(c => c.DesiredSize.Height);
            for (var c = 0; c < row.Count; c++)
                row[c].Arrange(new Rect(c * (itemWidth + ColumnSpacing), y, itemWidth, h));
            y += h + RowSpacing;
        }
        return finalSize;
    }
}
