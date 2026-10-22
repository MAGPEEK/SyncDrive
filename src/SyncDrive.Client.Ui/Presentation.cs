using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Status;
using SyncDrive.Core.Merge;

namespace SyncDrive.Client.Ui;

/// <summary>Farbcode des Zustands (Panel-Kopf, Abzeichen): Grün aktuell, Blau läuft, Grau angehalten, Rot Fehler, Blass getrennt.</summary>
public enum StatusColor
{
    Neutral,
    Green,
    Blue,
    Grey,
    Red,
    Pale,
}

/// <summary>Dateiart für das Symbol in Listen (farbige Dateityp-Symbole).</summary>
public enum FileKind
{
    Folder,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Image,
    Audio,
    Video,
    Archive,
    Other,
}

/// <summary>Was die Oberfläche öffnen kann. Die ViewModels kennen nur diese Schnittstelle, nicht Avalonia.</summary>
public interface IUiServices
{
    /// <summary>Führt <paramref name="action"/> im UI-Thread aus (Statusmeldungen kommen aus dem Hintergrund).</summary>
    void Post(Action action);

    Task<string?> PickLocalFolderAsync(string? suggestion);

    Task<(string SpaceId, string Path, string DisplayPath)?> PickServerFolderAsync(ServerFolderPickerViewModel picker);

    Task ShowMessageAsync(string message);

    Task<bool> ConfirmAsync(string message);

    void OpenFolder(string path);

    void OpenMainWindow(MainPage page);

    void OpenWizard();

    void ShowAbout();

    /// <summary>Kleine Meldung unten rechts, verschwindet von selbst (Desktop-Benachrichtigung).</summary>
    void ShowToast(string title, string message);

    /// <summary>Dialog „Edit Connection“; endet, wenn der Dialog schließt.</summary>
    Task ShowEditConnectionAsync(EditConnectionViewModel dialog);

    /// <summary>Dialog „Sync Rules“; endet, wenn der Dialog schließt.</summary>
    Task ShowSyncRulesAsync(SyncRulesViewModel dialog);

    /// <summary>Versionsliste aus dem Explorer-Menü; endet, wenn das Fenster schließt.</summary>
    Task ShowVersionsAsync(VersionsViewModel dialog);

    /// <summary>„Speichern unter“-Dialog; null bei Abbruch.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, string? folder);

    void Quit();
}

public enum MainPage
{
    SyncTasks,
    BackupTask,
    Logs,
    Notifications,
    GlobalSettings,
}

public static class Presentation
{
    public static string Word(TrayState state, Texts t) => state switch
    {
        TrayState.UpToDate => t.StateUpToDate,
        TrayState.Syncing => t.StateSyncing,
        TrayState.Paused => t.StatePaused,
        TrayState.Error => t.StateError,
        TrayState.Disconnected => t.StateDisconnected,
        _ => t.StateNormal,
    };

    public static StatusColor Color(TrayState state) => state switch
    {
        TrayState.UpToDate => StatusColor.Green,
        TrayState.Syncing => StatusColor.Blue,
        TrayState.Paused => StatusColor.Grey,
        TrayState.Error => StatusColor.Red,
        TrayState.Disconnected => StatusColor.Pale,
        _ => StatusColor.Neutral,
    };

    public static string Word(TaskState state, Texts t) => state switch
    {
        TaskState.Connecting => t.TaskConnecting,
        TaskState.Disabled => t.TaskDisabled,
        TaskState.Done => t.TaskDone,
        TaskState.Error => t.TaskError,
        TaskState.Paused => t.TaskPaused,
        _ => t.TaskSyncing,
    };

    public static string Direction(SyncDirection direction, Texts t) => direction switch
    {
        SyncDirection.DownloadOnly => t.DownloadOnly,
        SyncDirection.UploadOnly => t.UploadOnly,
        _ => t.TwoWay,
    };

    /// <summary>„just now“, „5 minute(s) ago“, „3 hour(s) ago“, „2 day(s) ago“ – Einheit mit Mehrzahlklammer.</summary>
    public static string RelativeTime(DateTimeOffset at, DateTimeOffset now, Texts t)
    {
        var age = now - at;
        return age.TotalMinutes < 1 ? t.JustNow
            : age.TotalHours < 1 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, t.MinutesAgo, (int)age.TotalMinutes)
            : age.TotalDays < 1 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, t.HoursAgo, (int)age.TotalHours)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, t.DaysAgo, (int)age.TotalDays);
    }

    /// <summary>„Uploaded“, „Deleted – Unsynced“: Aktion und, wenn nicht übertragen, der Zusatz.</summary>
    public static string Action(ActivityEntry entry, Texts t)
    {
        var action = entry.Action switch
        {
            ActivityAction.Upload => t.Uploaded,
            ActivityAction.Download => t.Downloaded,
            ActivityAction.Delete => t.Deleted,
            ActivityAction.Conflict => t.Conflict,
            _ => t.Moved,
        };
        return entry.Result == ActivityResult.Done ? action : $"{action} – {t.Unsynced}";
    }

    public static FileKind KindOf(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return FileKind.Folder;
        }

        return Path.GetExtension(name).ToUpperInvariant() switch
        {
            ".DOC" or ".DOCX" or ".ODT" or ".RTF" or ".TXT" or ".MD" => FileKind.Document,
            ".XLS" or ".XLSX" or ".ODS" or ".CSV" => FileKind.Spreadsheet,
            ".PPT" or ".PPTX" or ".ODP" => FileKind.Presentation,
            ".PDF" => FileKind.Pdf,
            ".JPG" or ".JPEG" or ".PNG" or ".GIF" or ".BMP" or ".HEIC" or ".WEBP" or ".SVG" => FileKind.Image,
            ".MP3" or ".WAV" or ".FLAC" or ".M4A" or ".OGG" => FileKind.Audio,
            ".MP4" or ".MOV" or ".MKV" or ".AVI" or ".WEBM" => FileKind.Video,
            ".ZIP" or ".7Z" or ".RAR" or ".TAR" or ".GZ" or ".ISO" => FileKind.Archive,
            _ => FileKind.Other,
        };
    }

    /// <summary>Lange Namen in der Mitte kürzen („Anfang…Ende.ext“).</summary>
    public static string MiddleEllipsis(string text, int max)
    {
        if (text.Length <= max || max < 5)
        {
            return text;
        }

        var tail = (max - 1) / 2;
        var head = max - 1 - tail;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
    }
}

/// <summary>Datei- und Ordnername aus Pfaden, die aus Windows stammen, auch wenn der Code anderswo läuft (Tests unter Linux).</summary>
public static class LocalPaths
{
    private static readonly char[] Separators = ['\\', '/'];

    public static string FileName(string path) => path.TrimEnd(Separators)[(path.TrimEnd(Separators).LastIndexOfAny(Separators) + 1)..];

    public static string? Folder(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var cut = trimmed.LastIndexOfAny(Separators);
        return cut > 0 ? trimmed[..cut] : null;
    }
}
