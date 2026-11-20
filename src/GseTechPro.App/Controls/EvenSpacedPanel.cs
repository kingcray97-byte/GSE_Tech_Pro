using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace GseTechPro.App.Controls;

/// <summary>
/// Places its children so their centers sit at even steps from one edge to the other
/// (first child centered on the start edge, last on the end edge). Used for scope axis labels
/// so each label lines up with a graticule division line whatever the plot size.
/// </summary>
public sealed class EvenSpacedPanel : Panel
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(Orientation),
        typeof(EvenSpacedPanel),
        new PropertyMetadata(Orientation.Vertical, (d, _) => ((EvenSpacedPanel)d).InvalidateArrange()));

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public static readonly DependencyProperty CrossAlignmentProperty = DependencyProperty.Register(
        nameof(CrossAlignment),
        typeof(HorizontalAlignment),
        typeof(EvenSpacedPanel),
        new PropertyMetadata(HorizontalAlignment.Right, (d, _) => ((EvenSpacedPanel)d).InvalidateArrange()));

    /// <summary>Vertical orientation only: whether labels hug the left or right edge.</summary>
    public HorizontalAlignment CrossAlignment
    {
        get => (HorizontalAlignment)GetValue(CrossAlignmentProperty);
        set => SetValue(CrossAlignmentProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxW = 0, maxH = 0;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children)
        {
            child.Measure(unbounded);
            maxW = Math.Max(maxW, child.DesiredSize.Width);
            maxH = Math.Max(maxH, child.DesiredSize.Height);
        }

        return Orientation == Orientation.Vertical
            ? new Size(maxW, double.IsInfinity(availableSize.Height) ? maxH * Children.Count : availableSize.Height)
            : new Size(double.IsInfinity(availableSize.Width) ? maxW * Children.Count : availableSize.Width, maxH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int n = Children.Count;
        for (int i = 0; i < n; i++)
        {
            var child = Children[i];
            var size = child.DesiredSize;
            double frac = n <= 1 ? 0.5 : i / (double)(n - 1);

            if (Orientation == Orientation.Vertical)
            {
                double y = Math.Clamp(frac * finalSize.Height - size.Height / 2, 0, Math.Max(0, finalSize.Height - size.Height));
                double x = CrossAlignment == HorizontalAlignment.Left ? 0 : finalSize.Width - size.Width;
                child.Arrange(new Rect(new Point(Math.Max(0, x), y), size));
            }
            else
            {
                double x = Math.Clamp(frac * finalSize.Width - size.Width / 2, 0, Math.Max(0, finalSize.Width - size.Width));
                child.Arrange(new Rect(new Point(x, 0), size));
            }
        }

        return finalSize;
    }
}
