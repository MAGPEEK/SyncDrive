using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SyncDrive.Client.Tray.Views;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray;

/// <summary>Echte Fenster und Dialoge hinter <see cref="IUiServices"/>; je Fensterart höchstens eines.</summary>
public sealed class WindowManager(IClassicDesktopStyleApplicationLifetime lifetime, Texts initialTexts, string suggestedLocalFolder) : IUiServices
{
    private ShellViewModel? _shell;
    private MainWindow? _main;
    private MainWindowViewModel? _mainModel;
    private WizardWindow? _wizard;
    private PanelWindow? _panel;

    private Texts CurrentTexts => _shell?.Texts ?? initialTexts;

    public void Attach(ShellViewModel shell) => _shell = shell;

    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    public void TogglePanel()
    {
        if (_shell is null)
        {
            return;
        }

        _panel ??= new PanelWindow();
        if (_panel.IsVisible)
        {
            _panel.Hide();
        }
        else
        {
            _panel.ShowNearTray(_shell.Panel);
        }
    }

    public async Task<string?> PickLocalFolderAsync(string? suggestion)
    {
        var owner = Owner();
        if (owner is null)
        {
            return null;
        }

        IStorageFolder? start = null;
        if (suggestion is not null)
        {
            var existing = Directory.Exists(suggestion) ? suggestion : Path.GetDirectoryName(suggestion);
            if (existing is not null)
            {
                start = await owner.StorageProvider.TryGetFolderFromPathAsync(existing);
            }
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = start,
            Title = CurrentTexts.YourComputer,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<(string SpaceId, string Path, string DisplayPath)?> PickServerFolderAsync(ServerFolderPickerViewModel picker)
    {
        var owner = Owner();
        return owner is null ? null : await new ServerFolderDialog(picker).ShowDialog<(string, string, string)?>(owner);
    }

    public async Task ShowMessageAsync(string message)
    {
        if (Owner() is { } owner)
        {
            await new MessageDialog(message, CurrentTexts, confirm: false).ShowDialog<bool>(owner);
        }
    }

    public async Task<bool> ConfirmAsync(string message) =>
        Owner() is { } owner && await new MessageDialog(message, CurrentTexts, confirm: true).ShowDialog<bool>(owner);

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    public void OpenMainWindow(MainPage page)
    {
        if (_shell is null)
        {
            return;
        }

        if (_main is null)
        {
            _mainModel = new MainWindowViewModel(_shell, this);
            _main = new MainWindow { DataContext = _mainModel };
        }

        _mainModel!.Page = page;
        _ = _mainModel.RefreshAsync();
        _main.Show();
        _main.Activate();
    }

    public void OpenWizard()
    {
        if (_shell?.Client is not { } client)
        {
            _ = ShowMessageAsync(CurrentTexts.DaemonNotRunning);
            return;
        }

        if (_wizard is { IsVisible: true })
        {
            _wizard.Activate();
            return;
        }

        var model = new WizardViewModel(client, this, CurrentTexts, suggestedLocalFolder);
        model.Finished += created =>
        {
            if (created && _mainModel is not null)
            {
                _ = _mainModel.RefreshAsync();
            }
        };
        _wizard = new WizardWindow(model);
        _ = model.LoadAsync(CancellationToken.None);
        _wizard.Show();
    }

    public async Task ShowVersionsAsync(VersionsViewModel dialog)
    {
        if (Owner() is { } owner)
        {
            await new VersionsWindow(dialog).ShowDialog(owner);
        }
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string? folder)
    {
        if (Owner() is not { } owner)
        {
            return null;
        }

        var start = folder is null ? null : await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await owner.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions { SuggestedFileName = suggestedName, SuggestedStartLocation = start, ShowOverwritePrompt = true });
        return file?.TryGetLocalPath();
    }

    public void ShowToast(string title, string message) => new ToastWindow(title, message).ShowBriefly();

    public async Task ShowEditConnectionAsync(EditConnectionViewModel dialog)
    {
        if (Owner() is { } owner)
        {
            await new EditConnectionWindow(dialog).ShowDialog(owner);
        }
    }

    public async Task ShowSyncRulesAsync(SyncRulesViewModel dialog)
    {
        if (Owner() is { } owner)
        {
            await new SyncRulesWindow(dialog).ShowDialog(owner);
        }
    }

    public void ShowAbout()
    {
        var version = SyncDrive.Contracts.ProductVersion.Current;
        _ = ShowMessageAsync($"{CurrentTexts.AppName} {version}\n\n{CurrentTexts.AboutText}");
    }

    public void Quit()
    {
        if (_main is not null)
        {
            _main.ReallyClose = true;
        }

        lifetime.Shutdown();
    }

    /// <summary>Dialoge brauchen ein Elternfenster: Assistent, sonst Hauptfenster (wird dafür geöffnet).</summary>
    private Window? Owner()
    {
        if (_wizard is { IsVisible: true })
        {
            return _wizard;
        }

        if (_main is not { IsVisible: true })
        {
            OpenMainWindow(_mainModel?.Page ?? MainPage.SyncTasks);
        }

        return _main;
    }
}
