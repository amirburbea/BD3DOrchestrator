using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BD3DOrchestrator;

public sealed class NegatedBooleanConverter : IValueConverter
{
    object IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture) => Negate(value);

    object IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Negate(value);

    private static object Negate(object value) => value switch
    {
        true => BooleanBoxes.False,
        false => BooleanBoxes.True,
        _ => DependencyProperty.UnsetValue,
    };
}

