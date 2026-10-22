using Avalonia.Controls;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray.Views;

public partial class WizardWindow : Window
{
    public WizardWindow()
    {
        InitializeComponent();
    }

    public WizardWindow(WizardViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.Finished += _ => Close();
    }
}
