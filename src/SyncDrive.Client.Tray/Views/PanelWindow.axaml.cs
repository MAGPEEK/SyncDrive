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

    public void ShowNearTray(PanelViewModel viewModel)
    {
        DataContext = viewModel;
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            // Windows: über der Taskleiste unten rechts; macOS: unter der Menüleiste oben rechts, dort sitzt das Symbol
            Position = new PixelPoint(
                area.Right - (int)(Width * scale) - (int)(12 * scale),
                OperatingSystem.IsMacOS() ? area.Y + (int)(6 * scale) : area.Bottom - (int)(Height * scale) - (int)(12 * scale));
        }

        Show();
        Activate();
        _ = viewModel.RefreshAsync(CancellationToken.None);
    }
}
