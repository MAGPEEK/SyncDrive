using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray.Views;

public partial class EditConnectionWindow : Window
{
    public EditConnectionWindow()
    {
        InitializeComponent();
    }

    public EditConnectionWindow(EditConnectionViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.Finished += _ => Close();
    }
}

public partial class SyncRulesWindow : Window
{
    public SyncRulesWindow()
    {
        InitializeComponent();
    }

    public SyncRulesWindow(SyncRulesViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.Finished += _ => Close();
    }
}

/// <summary>Desktop-Benachrichtigung: unten rechts über der Taskleiste, ohne Fokus zu nehmen; verschwindet nach einigen Sekunden.</summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(6);

    public ToastWindow()
    {
        InitializeComponent();
    }

    public ToastWindow(string title, string message)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
    }

    public void ShowBriefly()
    {
        Show();
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            Position = new PixelPoint(
                area.Right - (int)(Bounds.Width * scale) - (int)(12 * scale),
                area.Bottom - (int)(Bounds.Height * scale) - (int)(12 * scale));
        }

        DispatcherTimer.RunOnce(Close, Visible);
    }
}

public partial class VersionsWindow : Window
{
    public VersionsWindow()
    {
        InitializeComponent();
    }

    public VersionsWindow(VersionsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.Closed += Close;
    }
}
