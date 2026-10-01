using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Acapella.App.Layout;

namespace Acapella.App.Converters;

/// <summary>Maps the lane's dashed full-source bar (always [0, SourceDurationMs)) onto pixels within
/// the track's current width, via Canvas.Left ("X") or Width ("Width") depending on
/// ConverterParameter -- two bindings to the same three values rather than duplicating the span
/// math. Values: [0]=SourceDurationMs, [1]=TimelineDurationMs, [2]=trackWidthPx (ElementName binding
/// to the Canvas's own ActualWidth).</summary>
public class FullSourceBarGeometryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values.Any(v => v is not double)) return 0.0;
        double sourceDurationMs = (double)values[0];
        double timelineDurationMs = (double)values[1];
        double trackWidthPx = (double)values[2];

        var (x, width) = ClipTimelineMath.ToPixelRange(0, sourceDurationMs, timelineDurationMs, trackWidthPx);
        return parameter as string == "Width" ? width : x;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps the lane's solid trimmed-span bar ([TrimStartMs, EffectiveTrimEndMs)) onto pixels,
/// the same way as FullSourceBarGeometryConverter above. Values: [0]=TrimStartMs,
/// [1]=EffectiveTrimEndMs, [2]=TimelineDurationMs, [3]=trackWidthPx.</summary>
public class TrimmedBarGeometryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values.Any(v => v is not double)) return 0.0;
        double startMs = (double)values[0];
        double endMs = (double)values[1];
        double timelineDurationMs = (double)values[2];
        double trackWidthPx = (double)values[3];

        var (x, width) = ClipTimelineMath.ToPixelRange(startMs, endMs, timelineDurationMs, trackWidthPx);
        return parameter as string == "Width" ? width : x;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>One-way: is LayerRowViewModel.PitchMode equal to the mode named by ConverterParameter
/// ("Melodyne" or "Auto")? Drives the channel panel's two-button segmented switch; the switch's
/// own Click handlers (not a TwoWay binding) are what actually change PitchMode.</summary>
public class PitchModeEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>0..1 unit level (LayerRowViewModel.MeterLevel) to a pixel height within a fixed-height
/// meter track, for the lane's small vertical level bar. ConverterParameter is the track's full
/// height in device-independent pixels (a string, parsed once).</summary>
public class UnitToHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double level = value is double d ? d : 0;
        double maxHeight = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 36;
        return Math.Clamp(level, 0, 1) * maxHeight;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
