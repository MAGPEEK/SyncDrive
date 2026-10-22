using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Contracts;

namespace SyncDrive.Client.Ui;

/// <summary>
/// Aktion aus dem Explorer-Menü („syncdrive.exe --shell &lt;aktion&gt; &lt;pfad&gt;“): führt sie über den Daemon aus und meldet das Ergebnis.
/// Die Einträge sind statisch (ADR-10); passt eine Aktion nicht zum Zustand, erklärt die Meldung, was stattdessen geht.
/// </summary>
public sealed class ShellCommand(IShellActions shell, IUiServices ui, Texts texts)
{
    public static readonly IReadOnlyList<string> Verbs = ["versions", "getlink", "keep", "freeup", "stopsync", "resumesync"];

    public async Task RunAsync(string verb, string path, CancellationToken ct)
    {
        if (await shell.GetPathInfoAsync(path, ct) is null)
        {
            await ui.ShowMessageAsync(texts.NotInSyncFolder);
            return;
        }

        switch (verb)
        {
            case "versions":
                var versions = new VersionsViewModel(shell, ui, texts, path);
                await versions.LoadAsync(ct);
                await ui.ShowVersionsAsync(versions);
                break;
            case "getlink":
                var link = await shell.GetLinkAsync(path, ct);
                if (link is { Succeeded: true, Message: { } url })
                {
                    await ui.CopyAndShowAsync(url, string.Format(CultureInfo.CurrentCulture, texts.LinkCopied, url));
                }
                else
                {
                    await ui.ShowMessageAsync(Explain(link, texts));
                }

                break;
            case "keep" or "freeup":
                if (await shell.SetPinnedAsync(path, keep: verb == "keep", ct) is { Succeeded: false } pinned)
                {
                    await ui.ShowMessageAsync(Explain(pinned, texts)); // Erfolg zeigt der Explorer am Statussymbol
                }

                break;
            case "stopsync":
                await ReportAsync(await shell.SetFolderSyncedAsync(path, synced: false, ct), texts.FolderExcluded);
                break;
            case "resumesync":
                await ReportAsync(await shell.SetFolderSyncedAsync(path, synced: true, ct), texts.FolderIncluded);
                break;
            default:
                await ui.ShowMessageAsync($"{texts.ActionFailed} {verb}");
                break;
        }
    }

    private Task ReportAsync(ShellResult result, string success) => ui.ShowMessageAsync(result.Succeeded ? success : Explain(result, texts));

    public static string Explain(ShellResult result, Texts texts) => result.ErrorCode switch
    {
        ErrorCode.Forbidden => texts.NotAllowed,
        ShellErrors.NotInSyncFolder => texts.NotInSyncFolder,
        ShellErrors.NotPossible => texts.NotPossibleForRoot,
        ShellErrors.NotOnDemand => texts.NotOnDemand,
        ShellErrors.LinkNeedsWeb => texts.LinkNeedsWeb,
        ShellErrors.Unreachable => texts.ErrorUnreachable,
        _ => $"{texts.ActionFailed} {result.Message ?? result.ErrorCode}",
    };
}

/// <summary>Zeile der Versionsliste: Zeitpunkt, Größe, wer; die aktuelle Fassung ist markiert.</summary>
public sealed record VersionRow(FileVersion Version, string Time, string Size, string By, bool IsCurrent, string CurrentLabel = "");

/// <summary>„Browse previous versions“: Liste neueste zuerst; „Save as…“ lädt eine Version unter neuem Namen herunter, „Restore“ macht sie aktuell.</summary>
public sealed partial class VersionsViewModel(IShellActions shell, IUiServices ui, Texts texts, string path) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand), nameof(RestoreCommand))]
    private VersionRow? _selected;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isBusy;

    public event Action? Closed;

    public Texts Texts { get; } = texts;

    public string FileName { get; } = LocalPaths.FileName(path);

    public string Title => $"{Texts.VersionsTitle} – {FileName}";

    public ObservableCollection<VersionRow> Rows { get; } = [];

    public async Task LoadAsync(CancellationToken ct)
    {
        Rows.Clear();
        foreach (var version in await shell.ListVersionsAsync(path, ct))
        {
            Rows.Add(new VersionRow(
                version,
                version.MTime.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                Sizes.Format(version.Size),
                version.CreatedBy ?? string.Empty,
                version.IsCurrent,
                version.IsCurrent ? $"({Texts.CurrentVersion})" : string.Empty));
        }

        IsEmpty = Rows.Count == 0;
        Selected = Rows.FirstOrDefault(r => !r.IsCurrent);
    }

    private bool CanAct => Selected is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsAsync()
    {
        var row = Selected!;
        var suggested = $"{Path.GetFileNameWithoutExtension(FileName)} ({row.Version.MTime.ToLocalTime():yyyy-MM-dd HH-mm}){Path.GetExtension(FileName)}";
        if (await ui.PickSaveFileAsync(suggested, LocalPaths.Folder(path)) is not { } target)
        {
            return;
        }

        await RunAsync(() => shell.SaveVersionAsAsync(path, row.Version.Revision, target, CancellationToken.None), Texts.VersionSaved);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RestoreAsync()
    {
        var row = Selected!;
        if (!await ui.ConfirmAsync(Texts.ConfirmRestore))
        {
            return;
        }

        await RunAsync(() => shell.RestoreVersionAsync(path, row.Version.Revision, CancellationToken.None), Texts.VersionRestored);
        await LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    private async Task RunAsync(Func<Task<ShellResult>> action, string success)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            await ui.ShowMessageAsync(result.Succeeded ? success : ShellCommand.Explain(result, Texts));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
