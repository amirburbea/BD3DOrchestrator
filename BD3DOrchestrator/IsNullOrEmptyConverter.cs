using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BD3DOrchestrator;

public sealed class IsNullOrEmptyConverter : IValueConverter
{
    public bool Invert { get; set; }

    object IValueConverter.Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNullOrEmpty = value is null or "";
        return BooleanBoxes.Box(this.Invert ? !isNullOrEmpty : isNullOrEmpty);
    }

    object IValueConverter.ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
