using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OptimusVibe.App.Views;

/// <summary>Round coloured icon + big number + small unit (the Samsung Health style metric line).</summary>
public partial class MetricRow : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(MetricRow), new PropertyMetadata(""));
    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(MetricRow), new PropertyMetadata(Brushes.Gray));
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricRow), new PropertyMetadata(""));
    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(MetricRow), new PropertyMetadata(""));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush IconBrush { get => (Brush)GetValue(IconBrushProperty); set => SetValue(IconBrushProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public MetricRow() => InitializeComponent();
}
