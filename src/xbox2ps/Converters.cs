using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Xbox2Ps.ViewModels;

namespace Xbox2Ps;

/// <summary>Status dot colour.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    private static readonly Brush Off = Frozen(0x8A, 0x8A, 0x8A);
    private static readonly Brush Ok = Frozen(0x16, 0xC6, 0x0C);
    private static readonly Brush Warn = Frozen(0xFF, 0xB9, 0x00);
    private static readonly Brush Bad = Frozen(0xE8, 0x11, 0x23);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusLevel.Ok => Ok,
        StatusLevel.Warn => Warn,
        StatusLevel.Bad => Bad,
        _ => Off,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
