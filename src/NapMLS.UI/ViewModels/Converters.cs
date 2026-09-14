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

/// <summary>Int → bool: returns true when value == 1.</summary>
public sealed class IntToBoolConverter : IValueConverter
{
    public static readonly IntToBoolConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 1;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Int → bool: returns true when value == 2.</summary>
public sealed class IntToBoolConverter2 : IValueConverter
{
    public static readonly IntToBoolConverter2 Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 2;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Int → bool: returns true when value == 3.</summary>
public sealed class IntToBoolConverter3 : IValueConverter
{
    public static readonly IntToBoolConverter3 Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 3;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Int → bool: returns true when value == 4.</summary>
public sealed class IntToBoolConverter4 : IValueConverter
{
    public static readonly IntToBoolConverter4 Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 4;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Int → bool: returns true when value > 1 (show Back button).</summary>
public sealed class GreaterThanOneConverter : IValueConverter
{
    public static readonly GreaterThanOneConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i > 1;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Int → bool: returns true when value < 4 (show Next button).</summary>
public sealed class LessThanFourConverter : IValueConverter
{
    public static readonly LessThanFourConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i < 4;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Int → bool: returns true when value == 4 (show Create button).</summary>
public sealed class IsFourConverter : IValueConverter
{
    public static readonly IsFourConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 4;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>QqGroupOption → bool: true if parameter's QqGroupId matches.</summary>
public sealed class QqGroupToBoolConverter : IValueConverter
{
    public static readonly QqGroupToBoolConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is QqGroupOption selected && parameter is QqGroupOption option)
            return selected.QqGroupId == option.QqGroupId;
        return false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
