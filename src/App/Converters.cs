using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using InputAutomationTool.Core;

namespace InputAutomationTool.App;

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            LogLevel.Success => Brushes.SeaGreen,
            LogLevel.Fail => Brushes.DarkOrange,
            LogLevel.Error => Brushes.Firebrick,
            _ => Brushes.Black,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}
