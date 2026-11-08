using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LatentFolio.App.Controls;

public sealed class BoolToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool x && x;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NotEmptyToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true,
        };
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class IntToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Num : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is null) return "";
        if (value is double d) return d.ToString("0.##", CultureInfo.InvariantCulture);
        return value.ToString() ?? "";
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => null!;
}

/// <summary>double → GridLength：左栏宽度在 248 和 56 之间切换时绑 ColumnDefinition.Width。</summary>
public sealed class DoubleToGridLength : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is double d ? new GridLength(d) : new GridLength(248);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>#RRGGBB → 画刷。空串给一条淡淡的边框色（= 没设标记色）。</summary>
public sealed class HexToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s))
            return Application.Current?.TryFindResource("B.BorderStrong") as System.Windows.Media.Brush
                   ?? System.Windows.Media.Brushes.Gray;
        try
        {
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s));
        }
        catch
        {
            return System.Windows.Media.Brushes.Gray;
        }
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
