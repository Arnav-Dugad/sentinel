using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Sentinel.App.Controls;

/// <summary>
/// Lays children out in equal-width columns (each at least <see cref="MinItemWidth"/>) and wraps them into rows.
/// Rows are balanced so there is never a lone orphan: five tiles that fit four per row become 3 + 2 rather than 4 + 1,
/// and a shorter final row stretches to the full width so no gap is left at the end. Every row takes the height of
/// its tallest child, so cards in a row line up.
/// </summary>
public sealed partial class AdaptiveGrid : Panel
{
    public double MinItemWidth { get; set; } = 200;
    public double ColumnSpacing { get; set; } = 12;
    public double RowSpacing { get; set; } = 12;
    public int MaxColumns { get; set; } = 6;

    /// <summary>Spread items evenly over the rows that are needed (default true).</summary>
    public bool Balance { get; set; } = true;

    /// <summary>Let a shorter last row fill the available width (default true).</summary>
    public bool FillLastRow { get; set; } = true;

    private List<UIElement> Visible() => Children.Where(c => c.Visibility == Visibility.Visible).ToList();

    private int Columns(double width, int count)
    {
        if (count == 0) return 1;
        int fit;
        if (double.IsInfinity(width) || width <= 0) fit = Math.Min(count, MaxColumns);
        else fit = (int)Math.Floor((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing));
        fit = Math.Clamp(fit, 1, Math.Max(1, Math.Min(MaxColumns, count)));
        if (!Balance) return fit;
        var rows = (int)Math.Ceiling(count / (double)fit);
        return (int)Math.Ceiling(count / (double)rows);
    }

    /// <summary>Yields (row index, items in the row, width of each item in the row).</summary>
    private IEnumerable<(int Row, List<UIElement> Items, double ItemWidth)> Rows(List<UIElement> items, double width)
    {
        var cols = Columns(width, items.Count);
        var full = double.IsInfinity(width) ? MinItemWidth * cols + ColumnSpacing * (cols - 1) : width;
        var standard = (full - ColumnSpacing * (cols - 1)) / cols;
        for (var r = 0; r * cols < items.Count; r++)
        {
            var row = items.Skip(r * cols).Take(cols).ToList();
            var w = FillLastRow && row.Count < cols ? (full - ColumnSpacing * (row.Count - 1)) / row.Count : standard;
            yield return (r, row, Math.Max(0, w));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Visible();
        double height = 0;
        var first = true;
        foreach (var (_, row, w) in Rows(items, availableSize.Width))
        {
            double h = 0;
            foreach (var child in row)
            {
                child.Measure(new Size(w, double.PositiveInfinity));
                h = Math.Max(h, child.DesiredSize.Height);
            }
            height += (first ? 0 : RowSpacing) + h;
            first = false;
        }
        // Collapsed children still need a measure pass.
        foreach (var hidden in Children.Where(c => c.Visibility != Visibility.Visible)) hidden.Measure(new Size(0, 0));
        var cols = Columns(availableSize.Width, items.Count);
        var width = double.IsInfinity(availableSize.Width) ? MinItemWidth * cols + ColumnSpacing * (cols - 1) : availableSize.Width;
        return new Size(items.Count == 0 ? 0 : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var (_, row, w) in Rows(Visible(), finalSize.Width))
        {
            var h = row.Max(c => c.DesiredSize.Height);
            for (var c = 0; c < row.Count; c++)
                row[c].Arrange(new Rect(c * (w + ColumnSpacing), y, w, h));
            y += h + RowSpacing;
        }
        return finalSize;
    }
}
