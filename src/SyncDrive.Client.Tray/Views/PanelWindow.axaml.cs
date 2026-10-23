using Avalonia;
using Avalonia.Controls;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray.Views;

/// <summary>Popup am Tray-Symbol; erscheint unten rechts über der Taskleiste und schließt bei Fokusverlust.</summary>
public partial class PanelWindow : Window
{
    public PanelWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            Hide();
            HiddenAt = DateTimeOffset.UtcNow;
        };
    }

    /// <summary>Wann das Panel zuletzt durch Fokusverlust zuging (ein Klick aufs Symbol soll es dann nicht sofort neu öffnen).</summary>
    public DateTimeOffset HiddenAt { get; private set; } = DateTimeOffset.MinValue;

    /// <param name="anchor">macOS: Lage des Menüleisten-Symbols in Punkten (oben links); das Panel öffnet mittig darunter.</param>
    public void ShowNearTray(PanelViewModel viewModel, (double X, double Y, double Width, double Height)? anchor = null)
    {
        DataContext = viewModel;
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            if (anchor is { } icon)
            {
                // Mittig unter dem Symbol, 4 Punkte Abstand; nicht über den Bildschirmrand hinaus
                var x = (int)((icon.X + (icon.Width / 2) - (Width / 2)) * scale);
                var y = (int)((icon.Y + icon.Height + 4) * scale);
                var margin = (int)(8 * scale);
                Position = new PixelPoint(
                    Math.Clamp(x, area.X + margin, area.Right - (int)(Width * scale) - margin),
                    Math.Max(y, area.Y));
            }
            else
            {
                // Windows: über der Taskleiste unten rechts (dort sitzt der Infobereich)
                Position = new PixelPoint(
                    area.Right - (int)(Width * scale) - (int)(12 * scale),
                    area.Bottom - (int)(Height * scale) - (int)(12 * scale));
            }
        }

        Show();
        Activate();
        _ = viewModel.RefreshAsync(CancellationToken.None);
    }
}
