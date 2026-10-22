using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SyncDrive.Client.Tray.Views;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray;

/// <summary>
/// Oberflächendienste für den Kurzprozess aus dem Explorer-Menü (<c>--shell</c>): kein Tray, kein Hauptfenster; Dialoge stehen für sich und
/// werden abgewartet. Danach endet der Prozess.
/// </summary>
public sealed class ShellModeUi(Texts texts) : IUiServices
{
    private Window? _current;

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public async Task ShowMessageAsync(string message) => await ShowAndWaitAsync(new MessageDialog(message, texts, confirm: false));

    /// <summary>Die Zwischenablage gehört zu einem Fenster: kopiert wird, sobald die Meldung offen ist.</summary>
    public async Task CopyAndShowAsync(string text, string message)
    {
        var dialog = new MessageDialog(message, texts, confirm: false);
        dialog.Opened += async (_, _) =>
        {
            if (dialog.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        };
        await ShowAndWaitAsync(dialog);
    }

    public async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new MessageDialog(message, texts, confirm: true);
        var result = false;
        dialog.Closed += (_, _) => result = dialog.Result;
        await ShowAndWaitAsync(dialog);
        return result;
    }

    public Task ShowVersionsAsync(VersionsViewModel dialog) => ShowAndWaitAsync(new VersionsWindow(dialog));

    public async Task<string?> PickSaveFileAsync(string suggestedName, string? folder)
    {
        if (_current is null)
        {
            return null;
        }

        var start = folder is null ? null : await _current.StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await _current.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public Task<string?> PickLocalFolderAsync(string? suggestion) => Task.FromResult<string?>(null);

    public Task<(string SpaceId, string Path, string DisplayPath)?> PickServerFolderAsync(ServerFolderPickerViewModel picker) =>
        Task.FromResult<(string, string, string)?>(null);

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
    }

    public void OpenMainWindow(MainPage page)
    {
    }

    public void OpenWizard()
    {
    }

    public void ShowAbout()
    {
    }

    public void ShowToast(string title, string message)
    {
    }

    public Task ShowEditConnectionAsync(EditConnectionViewModel dialog) => Task.CompletedTask;

    public Task ShowSyncRulesAsync(SyncRulesViewModel dialog) => Task.CompletedTask;

    public void Quit()
    {
    }

    private async Task ShowAndWaitAsync(Window window)
    {
        var previous = _current;
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowInTaskbar = true;
        _current = window;
        window.Show();
        window.Activate();
        await closed.Task;
        _current = previous;
    }
}
