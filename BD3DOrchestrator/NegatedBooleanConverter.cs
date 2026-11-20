using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BD3DOrchestrator;

public sealed class NegatedBooleanConverter : IValueConverter
{
    /// <summary>Cached boxed <c>true</c>.</summary>
    private static readonly object _true = true;
    /// <summary>Cached boxed <c>false</c>.</summary>
    private static readonly object _false = false;

    object IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture) => Negate(value);

    object IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Negate(value);

    private static object Negate(object value) => value switch
    {
        true => _false,
        false => _true,
        _ => DependencyProperty.UnsetValue,
    };
}

