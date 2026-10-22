using System.Windows;
using System.Windows.Controls;

namespace OptimusVibe.App.Views;

/// <summary>
/// One horizontal row that never wraps: every child but the first gets the width it wants, the first one gets whatever is left
/// (so its text can trim with an ellipsis) and the row stays a single line.
/// </summary>
public sealed class TrimFirstPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double used = 0, height = 0;
        for (var i = 1; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            used += c.DesiredSize.Width;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        double first = 0;
        if (InternalChildren.Count > 0)
        {
            var left = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width - used);
            InternalChildren[0].Measure(new Size(left, available.Height));
            first = InternalChildren[0].DesiredSize.Width;
            height = Math.Max(height, InternalChildren[0].DesiredSize.Height);
        }
        return new Size(first + used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double used = 0;
        for (var i = 1; i < InternalChildren.Count; i++) used += InternalChildren[i].DesiredSize.Width;
        double x = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            var w = i == 0 ? Math.Min(c.DesiredSize.Width, Math.Max(0, finalSize.Width - used)) : c.DesiredSize.Width;
            c.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}
