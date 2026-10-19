using System.Globalization;
using System.Windows;
using System.Windows.Data;
using OptimusVibe.App.Services;

namespace OptimusVibe.App.Converters;

/// <summary>true → Visible. ConverterParameter "invert" flips it. Non-bool values: non-null / non-empty string / non-zero count → true.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value switch
        {
            bool b => b,
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            _ => true,
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Element height → CornerRadius of half that height: a true pill. WPF does not clamp oversized radii (999 renders
/// as an oval), so pills bind to their own ActualHeight instead.
/// </summary>
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new CornerRadius(value is double h && h > 0 ? h / 2 : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Markdown string → FlowDocument (a new one each time: a document can only sit in one viewer). Parameter: font size.</summary>
public sealed class MarkdownDocumentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        MarkdownFlow.ToDocument(value as string,
            parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 14);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
