using System.Globalization;
using System.Windows.Data;

namespace SomaFM.Windows;

public sealed class SliderFillConverter : IMultiValueConverter
{
    public static SliderFillConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values.Any(value => value is not double))
            return 0d;

        var (value, min, max, width) = ((double)values[0], (double)values[1], (double)values[2], (double)values[3]);
        var range = max - min;
        var travel = Math.Max(0, width - 12);
        return range <= 0 ? 0d : Math.Clamp((value - min) / range, 0, 1) * travel;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
