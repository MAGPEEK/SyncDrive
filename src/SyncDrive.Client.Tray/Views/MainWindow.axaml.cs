using Avalonia.Controls;

namespace SyncDrive.Client.Tray.Views;

/// <summary>Hauptfenster; Schließen versteckt es nur, die App bleibt im Tray.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (!ReallyClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>Beim Beenden der App wirklich schließen.</summary>
    public bool ReallyClose { get; set; }
}
