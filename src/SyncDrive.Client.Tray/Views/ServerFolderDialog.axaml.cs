using Avalonia.Controls;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray.Views;

/// <summary>Ordnerauswahl auf dem Server: Spaces oben, Ordner werden beim Aufklappen nachgeladen.</summary>
public partial class ServerFolderDialog : Window
{
    public ServerFolderDialog()
    {
        InitializeComponent();
    }

    public ServerFolderDialog(ServerFolderPickerViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        OkButton.Click += (_, _) => Close(viewModel.Result);
        CancelButton.Click += (_, _) => Close(null);
    }
}
