using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AirDrop.App.Services;

namespace AirDrop.App.Views;

/// <summary>Contact photo bytes → small frozen bitmap (null when missing / undecodable).</summary>
public sealed class PhotoConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] b ? ImageUtil.FromBytes(b, 160) : null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → Visible; "invert" parameter flips. Non-bool values: non-null / non-empty string / non-zero int = true.</summary>
public sealed class VisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool v = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "invert") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → frozen brush.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#8E8E93"));
            b.Freeze();
            return b;
        }
        catch (FormatException) { return Brushes.Gray; }
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

