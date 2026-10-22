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
        Deactivated += (_, _) => Hide();
    }

    public void ShowNearTray(PanelViewModel viewModel)
    {
        DataContext = viewModel;
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            Position = new PixelPoint(
                area.Right - (int)(Width * scale) - (int)(12 * scale),
                area.Bottom - (int)(Height * scale) - (int)(12 * scale));
        }

        Show();
        Activate();
        _ = viewModel.RefreshAsync(CancellationToken.None);
    }
}
