using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>Ein Eintrag im Papierkorb mit seinem Space.</summary>
public sealed record TrashRow(SpaceSummary Space, TrashEntry Entry)
{
    public string Key => Space.Id + "/" + Entry.NodeId;

    public bool IsFolder => Entry.Kind == NodeKinds.Directory;

    /// <summary>„Ort“: wo das Element lag, z. B. „My Drive / Projekte“.</summary>
    public string Location(Func<SpaceSummary, string> spaceLabel) =>
        Entry.OriginalPath.Contains('/')
            ? $"{spaceLabel(Space)} / {Entry.OriginalPath[..Entry.OriginalPath.LastIndexOf('/')].Replace("/", " / ", StringComparison.Ordinal)}"
            : spaceLabel(Space);
}

/// <summary>Der Papierkorb aller Spaces (Spalten: Titel, gelöscht am, Größe, Ort); gewählte Elemente wiederherstellen.</summary>
public sealed class TrashViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    public ListState State { get; private set; } = ListState.Loading;

    public IReadOnlyList<TrashRow> Rows { get; private set; } = [];

    public IReadOnlyCollection<string> Selected => _selected;

    public IReadOnlyList<TrashRow> SelectedRows => [.. Rows.Where(r => _selected.Contains(r.Key))];

    /// <summary>Wiederherstellen braucht Schreibrecht in jedem Space der Auswahl.</summary>
    public bool CanRestore => _selected.Count > 0 && SelectedRows.All(r => CanWriteIn(r.Space));

    public async Task LoadAsync(IReadOnlyList<SpaceSummary> spaces, CancellationToken ct)
    {
        State = ListState.Loading;
        OnChanged();
        var rows = new List<TrashRow>();
        await RunAsync(async () =>
        {
            foreach (var space in spaces)
            {
                rows.AddRange((await api.ListTrashAsync(space.Id, ct)).Select(e => new TrashRow(space, e)));
            }
        });
        Rows = [.. rows.OrderByDescending(r => r.Entry.DeletedAt).ThenBy(r => r.Entry.Name, StringComparer.CurrentCultureIgnoreCase)];
        _selected.IntersectWith(Rows.Select(r => r.Key));
        State = Rows.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    public void Select(TrashRow row, bool toggle = false)
    {
        if (!toggle)
        {
            _selected.Clear();
        }

        if (!_selected.Add(row.Key) && toggle)
        {
            _selected.Remove(row.Key);
        }

        OnChanged();
    }

    public void SelectForContextMenu(TrashRow row)
    {
        if (!_selected.Contains(row.Key))
        {
            Select(row);
        }
    }

    /// <summary>Stellt die Auswahl am ursprünglichen Ort wieder her (samt allem, was mit einem Ordner gelöscht wurde) und lädt neu.</summary>
    public async Task RestoreSelectedAsync(IReadOnlyList<SpaceSummary> spaces, CancellationToken ct)
    {
        var restored = 0;
        foreach (var row in SelectedRows)
        {
            if (!await RunAsync(() => api.RestoreFromTrashAsync(row.Space.Id, row.Entry.NodeId, ct)))
            {
                break;
            }

            restored++;
        }

        _selected.Clear();
        var message = Message;
        await LoadAsync(spaces, ct);
        if (message is not null)
        {
            Fail(message); // ein Fehler bleibt sichtbar, auch wenn danach neu geladen wird
        }
        else if (restored > 0)
        {
            Inform(string.Format(CultureInfo.CurrentCulture, Texts.Restored, restored));
        }
    }
}

/// <summary>Versionen einer Datei („Browse previous versions“): Liste mit der aktuellen markiert, Herunterladen, Wiederherstellen.</summary>
public sealed class VersionsViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public SpaceSummary? Space { get; private set; }

    public DriveItem? File { get; private set; }

    public ListState State { get; private set; } = ListState.Loading;

    public IReadOnlyList<VersionInfo> Versions { get; private set; } = [];

    public bool CanRestore => CanWriteIn(Space);

    public bool IsCurrent(VersionInfo version) => version.Revision == File?.Revision;

    public string Title => string.Format(CultureInfo.CurrentCulture, Texts.VersionsOf, File?.Name);

    public async Task OpenAsync(SpaceSummary space, DriveItem file, CancellationToken ct)
    {
        (Space, File) = (space, file);
        await LoadAsync(ct);
    }

    public string DownloadUrl(VersionInfo version) =>
        api.DownloadUrl(Space!.Id, File!.Path, IsCurrent(version) ? null : version.Revision);

    /// <summary>Macht die Version zur aktuellen; die Liste bekommt dabei einen neuen obersten Eintrag.</summary>
    public async Task<bool> RestoreAsync(VersionInfo version, CancellationToken ct)
    {
        if (IsCurrent(version) || !CanRestore)
        {
            return false;
        }

        long revision = 0;
        if (!await RunAsync(async () => revision = await api.RestoreVersionAsync(Space!.Id, File!.Path, version.Revision, File.Revision, ct)))
        {
            return false;
        }

        File = File! with { Revision = revision, Size = version.Size };
        await LoadAsync(ct);
        Inform(Texts.VersionRestored);
        return true;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        State = ListState.Loading;
        OnChanged();
        IReadOnlyList<VersionInfo> versions = [];
        await RunAsync(async () => versions = await api.ListVersionsAsync(Space!.Id, File!.Path, ct));
        Versions = versions;
        State = versions.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }
}
