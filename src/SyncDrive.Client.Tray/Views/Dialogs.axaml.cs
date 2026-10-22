using Avalonia.Controls;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray.Views;

/// <summary>Meldung (Karte mit „i“ und OK) oder Rückfrage (OK/Abbrechen), z. B. Regelverstoß bei der Ordnerwahl.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public MessageDialog(string message, Texts texts, bool confirm)
        : this()
    {
        MessageText.Text = message;
        OkButton.Content = texts.Ok;
        CancelButton.Content = texts.Cancel;
        CancelButton.IsVisible = confirm;
        OkButton.Click += (_, _) =>
        {
            Result = true;
            Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>OK gewählt (auch ohne modales Elternfenster abfragbar).</summary>
    public bool Result { get; private set; }
}
