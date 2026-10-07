using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DevBR.App.Controls;

/// <summary>Collapsed when the value is null, false, an empty string or zero.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps an enum to an index (for TabControl.SelectedIndex) and back.</summary>
public sealed class EnumIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Enum e ? System.Convert.ToInt32(e, CultureInfo.InvariantCulture) : 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int index && index >= 0 ? Enum.ToObject(targetType, index) : Binding.DoNothing;
}
