using System.Globalization;
using Avalonia.Data.Converters;

namespace A_Capella.App.Converters;

/// <summary>0..1 unit level (LayerRowViewModel.MeterLevel) to a pixel height within a fixed-height
/// meter track, for the lane's small vertical level bar. ConverterParameter is the track's full
/// height in device-independent pixels (a string, parsed once).</summary>
public class UnitToHeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double level = value is double d ? d : 0;
        double maxHeight = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 36;
        return Math.Clamp(level, 0, 1) * maxHeight;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
