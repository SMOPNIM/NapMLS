using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NapMLS.UI.ViewModels;

/// <summary>Converts bool to Brush for connection status indicator.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? Brushes.Green : Brushes.Red;
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts bool (IsOwn) to Brush for message background.</summary>
public sealed class OwnMessageBrushConverter : IValueConverter
{
    public static readonly OwnMessageBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isOwn && isOwn)
            return new SolidColorBrush(Color.Parse("#E3F2FD")); // light blue for own
        return new SolidColorBrush(Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
