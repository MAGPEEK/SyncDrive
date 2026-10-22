using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.Tray.Controls;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray;

/// <summary>Bindungshelfer der Ansichten.</summary>
public static class Converters
{
    public static readonly IValueConverter TrayBadge = new FuncValueConverter<TrayState, BadgeKind>(Badges.For);

    public static readonly IValueConverter TaskBadge = new FuncValueConverter<TaskState, BadgeKind>(Badges.For);

    public static readonly IValueConverter IsNormal = new FuncValueConverter<TrayState, bool>(s => s == TrayState.Normal);

    public static readonly IValueConverter StatusBrush = new FuncValueConverter<StatusColor, IBrush>(c => new SolidColorBrush(Palette.Of(c)));

    /// <summary>Gelöschte Einträge im Panel ausgegraut.</summary>
    public static readonly IValueConverter DimmedOpacity = new FuncValueConverter<bool, double>(dimmed => dimmed ? 0.45 : 1);

    public static readonly IValueConverter NotNull = new FuncValueConverter<object?, bool>(v => v is not null);

    public static readonly IValueConverter Chevron = new FuncValueConverter<bool, string>(expanded => expanded ? "▴" : "▾");

    public static readonly IValueConverter PortText = new PortConverter();

    private sealed class PortConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is decimal d ? d.ToString("0", culture) : string.Empty;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            decimal.TryParse(value as string, NumberStyles.Integer, culture, out var port) ? port : null;
    }
}
