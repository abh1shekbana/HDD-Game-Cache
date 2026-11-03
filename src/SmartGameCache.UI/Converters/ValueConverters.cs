using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SmartGameCache.Core.Models;

namespace SmartGameCache.UI.Converters;

public class BytesToFormattedStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return FormatBytes(bytes);
        }
        if (value is double dBytes)
        {
            return FormatBytes((long)dBytes);
        }
        return "0 B";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int digitGroups = (int)(Math.Log10(bytes) / Math.Log10(1024));
        if (digitGroups >= units.Length) digitGroups = units.Length - 1;
        double num = bytes / Math.Pow(1024, digitGroups);
        return $"{num:0.##} {units[digitGroups]}";
    }
}

public class SpeedToFormattedStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double speed)
        {
            return $"{BytesToFormattedStringConverter.FormatBytes((long)speed)}/s";
        }
        return "0 B/s";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StorageTypeToBadgeColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is StorageMediaType type)
        {
            return type switch
            {
                StorageMediaType.HDD => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red / Warning: mechanical HDD
                StorageMediaType.SSD => new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue: SATA SSD
                StorageMediaType.NVMe => new SolidColorBrush(Color.FromRgb(16, 185, 129)), // Emerald: NVMe SSD
                _ => new SolidColorBrush(Color.FromRgb(156, 163, 175))
            };
        }
        return new SolidColorBrush(Color.FromRgb(156, 163, 175));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            bool res = v == Visibility.Visible;
            return Invert ? !res : res;
        }
        return false;
    }
}

