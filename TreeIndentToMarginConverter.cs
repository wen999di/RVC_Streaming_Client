using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ClientAvalonia;

public sealed class TreeIndentToMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var indent = value switch
        {
            double number => number,
            float number => number,
            int number => number,
            _ => 0.0,
        };

        var bottom = parameter is string text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedBottom)
                ? Math.Max(0.0, parsedBottom)
                : 8.0;

        return new Thickness(Math.Max(0.0, indent), 0, 0, bottom);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
