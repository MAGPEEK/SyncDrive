using System.Globalization;
using SyncDrive.Contracts;
using SyncDrive.Core.Paths;

namespace SyncDrive.Web.App;

public enum ListState
{
    Loading,
    Ready,
    Empty,
}

public enum SortColumn
{
    Name,
    Modified,
    Size,
}

/// <summary>Ein Glied der Pfadleiste: Anzeigename und Pfad im Space.</summary>
public sealed record Crumb(string Label, string Path);

/// <summary>Eine Datei zum Hochladen, wie sie der Browser liefert (nur einmal vorwärts lesbar).</summary>
public sealed record UploadFile(string Name, long Size, DateTimeOffset Modified, Func<Stream> Open);

/// <summary>Laufendes Hochladen für die Fortschrittsanzeige.</summary>
public sealed record UploadProgress(string Name, long Size, long Sent);

/// <summary>
/// Der Dateibrowser („My Drive“): Ordner öffnen, Pfadleiste, Sortierung (Ordner zuerst), Auswahl wie im Explorer (Strg, Umschalt),
/// Anlegen, Umbenennen, Löschen, Hochladen, Herunterladen. Änderungen anderer Geräte kommen über den Feed ohne Neuladen.
/// Fehler des Servers werden zu einer Meldung in <see cref="Message"/>; nichts davon wirft bis in die Oberfläche.
/// </summary>
public sealed class BrowserViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly List<UploadProgress> _uploads = [];
    private IReadOnlyList<DriveItem> _items = [];
    private string? _anchor;

    public IReadOnlyList<SpaceSummary> Spaces { get; private set; } = [];

    public SpaceSummary? Space { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public ListState State { get; private set; } = ListState.Loading;

    public SortColumn Sort { get; private set; } = SortColumn.Name;

    public bool Descending { get; private set; }

    public IReadOnlyList<UploadProgress> Uploads => _uploads;

    /// <summary>Ordner zuerst, darin nach der gewählten Spalte.</summary>
    public IReadOnlyList<DriveItem> Items => [.. _items
        .OrderBy(i => i.IsFolder ? 0 : 1)
        .ThenBy(i => i, Comparer<DriveItem>.Create(Compare))];

    public IReadOnlyCollection<string> Selected => _selected;

    public IReadOnlyList<DriveItem> SelectedItems => [.. Items.Where(i => _selected.Contains(i.Path))];

    /// <summary>Die Auswahl mit ihrem Space, für Favoriten und Labels.</summary>
    public IReadOnlyList<LocatedItem> SelectedLocated => Space is null ? [] : [.. SelectedItems.Select(i => new LocatedItem(Space, i))];

    /// <summary>Schreiben erlaubt (Editor oder mehr); sonst sind Erstellen, Hochladen, Umbenennen und Löschen aus.</summary>
    public bool CanWrite => CanWriteIn(Space);

    public IReadOnlyList<Crumb> Breadcrumbs
    {
        get
        {
            if (Space is null)
            {
                return [];
            }

            var crumbs = new List<Crumb> { new(SpaceLabel(Space), string.Empty) };
            var parts = Path.Length == 0 ? [] : Path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                crumbs.Add(new Crumb(parts[i], string.Join('/', parts[..(i + 1)])));
            }

            return crumbs;
        }
    }

    /// <summary>Der eigene Space heißt „My Drive“; geteilte zeigen ihren Namen.</summary>
    public string SpaceLabel(SpaceSummary space) => space.Role == "owner" && space == Spaces.FirstOrDefault(s => s.Role == "owner")
        ? Texts.MyDrive
        : space.Name;

    /// <summary>Freigabe-Richtlinien: der Reiter „Öffentlicher Link“ und „Dateien anfordern“ erscheinen nur, wenn der Server sie erlaubt.</summary>
    public SharingPolicies Policies { get; private set; } = new();

    /// <summary>Liest die Richtlinien neu (beim Start und nach dem Ändern in der Verwaltung); ohne Antwort bleiben die Vorgaben.</summary>
    public async Task RefreshPoliciesAsync(CancellationToken ct)
    {
        await RunAsync(async () => Policies = await api.GetPoliciesAsync(ct));
        OnChanged();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await RunAsync(async () =>
        {
            Spaces = await api.ListSpacesAsync(ct);
            Space = Spaces.FirstOrDefault(s => s.Role == "owner") ?? (Spaces.Count > 0 ? Spaces[0] : null);
        });
        await RefreshPoliciesAsync(ct);
        await LoadAsync(ct);
    }

    /// <summary>Liest die Spaces neu (z. B. nach „hat mit Ihnen geteilt“); der offene Ordner bleibt.</summary>
    public async Task ReloadSpacesAsync(CancellationToken ct)
    {
        if (await RunAsync(async () => Spaces = await api.ListSpacesAsync(ct)))
        {
            Space = Spaces.FirstOrDefault(s => s.Id == Space?.Id) ?? Space;
            OnChanged();
        }
    }

    /// <summary>„Mit mir geteilt“: Ordner, die andere mit dem Benutzer geteilt haben (Team-Ordner und eigene Spaces ausgenommen).</summary>
    public IReadOnlyList<SpaceSummary> SharedWithMe => [.. Spaces.Where(s => s.SharedBy is not null)];

    /// <summary>Teilen darf, wer den ganzen Space verwaltet; in einem geteilten Ordner wird nicht weitergeteilt (ADR-12).</summary>
    public bool CanShare => Space is { SharedBy: null, Role: "manager" or "owner" };

    /// <summary>Verlässt einen geteilten Ordner; stand man darin, geht es zurück zu „My Drive“.</summary>
    public async Task<bool> LeaveShareAsync(SpaceSummary share, CancellationToken ct)
    {
        if (!await RunAsync(() => api.RemoveInviteAsync(share.Id, ct)))
        {
            return false;
        }

        await ReloadSpacesAsync(ct);
        if (Space?.Id == share.Id)
        {
            await OpenSpaceAsync(Spaces.FirstOrDefault(s => s.Role == "owner") ?? Spaces[0], ct);
        }

        return true;
    }

    public async Task OpenSpaceAsync(SpaceSummary space, CancellationToken ct)
    {
        Space = space;
        Path = string.Empty;
        await LoadAsync(ct);
    }

    /// <summary>„Speicherort öffnen“ aus Suche, Zuletzt oder Papierkorb: Space und Ordner öffnen, den Eintrag wählen.</summary>
    public async Task RevealAsync(SpaceSummary space, string path, CancellationToken ct)
    {
        Space = space;
        Path = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
        await LoadAsync(ct);
        if (_items.FirstOrDefault(i => i.Path == path) is { } item)
        {
            Select(item);
        }
    }

    public Task NavigateAsync(string path, CancellationToken ct)
    {
        Path = path;
        return LoadAsync(ct);
    }

    /// <summary>Doppelklick: Ordner öffnen; Dateien lädt die Oberfläche über <see cref="DownloadUrl"/>.</summary>
    public Task OpenAsync(DriveItem item, CancellationToken ct) => item.IsFolder ? NavigateAsync(item.Path, ct) : Task.CompletedTask;

    public string DownloadUrl(DriveItem item) => api.DownloadUrl(Space!.Id, item.Path);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    private readonly HashSet<string> _failedThumbnails = new(StringComparer.Ordinal);

    /// <summary>Vorschaubild statt Symbol: nur für Bilder (wie der Server) und nicht, wenn es schon einmal nicht geladen werden konnte.</summary>
    public string? ThumbnailUrl(DriveItem item, bool large = false)
    {
        if (item.IsFolder || !ImageExtensions.Contains(System.IO.Path.GetExtension(item.Name)))
        {
            return null;
        }

        var url = api.ThumbnailUrl(Space!.Id, item.Path, item.Revision, large);
        return _failedThumbnails.Contains(url) ? null : url;
    }

    /// <summary>Der Server hatte keine Vorschau (beschädigtes Bild, zu groß): künftig das Symbol zeigen.</summary>
    public void ThumbnailFailed(string url) => _failedThumbnails.Add(url);

    /// <summary>
    /// „Herunterladen“ der Auswahl: eine Datei direkt, Ordner oder mehrere Elemente als ZIP. Null ohne Auswahl oder wenn die Adresse für
    /// den Server zu lang würde (sehr viele Elemente; dann den Ordner selbst herunterladen).
    /// </summary>
    public string? SelectionDownloadUrl => SelectedItems switch
    {
        [] => null,
        [{ IsFolder: false } file] => DownloadUrl(file),
        var items => api.ZipUrl(Space!.Id, [.. items.Select(i => i.Path)]) is { Length: <= MaxUrlLength } url ? url : null,
    };

    /// <summary>Dateiname, den der Browser vorschlägt (der Server setzt ihn ebenfalls).</summary>
    public string SelectionDownloadName => SelectedItems switch
    {
        [{ IsFolder: false } file] => file.Name,
        [var folder] => folder.Name + ".zip",
        _ => (Breadcrumbs is [.., var folder] && Path.Length > 0 ? folder.Label : Texts.AppName) + ".zip",
    };

    /// <summary>Kestrel nimmt Anfragezeilen bis 8 KiB an; mit Puffer für Methode und Protokoll.</summary>
    public const int MaxUrlLength = 8000;

    /// <summary>Lädt den Ordner neu; die Auswahl bleibt, soweit die Einträge noch da sind.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (Space is null)
        {
            State = ListState.Empty;
            OnChanged();
            return;
        }

        if (_items.Count == 0)
        {
            State = ListState.Loading;
            OnChanged();
        }

        var path = Path;
        await RunAsync(async () =>
        {
            var items = await api.ListAsync(Space.Id, path, ct);
            if (path != Path)
            {
                return; // inzwischen woanders hin navigiert
            }

            _items = items;
            _selected.IntersectWith(items.Select(i => i.Path));
        });
        State = _items.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    public void SortBy(SortColumn column)
    {
        Descending = column == Sort && !Descending;
        Sort = column;
        OnChanged();
    }

    /// <summary>Klick: nur dieser; mit Strg dazu oder weg; mit Umschalt der Bereich ab dem letzten Klick.</summary>
    public void Select(DriveItem item, bool toggle = false, bool range = false)
    {
        var items = Items;
        if (range && _anchor is not null && items.FirstOrDefault(i => i.Path == _anchor) is { } anchor)
        {
            var from = IndexOf(items, anchor);
            var to = IndexOf(items, item);
            _selected.Clear();
            foreach (var i in items.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1))
            {
                _selected.Add(i.Path);
            }
        }
        else if (toggle)
        {
            if (!_selected.Remove(item.Path))
            {
                _selected.Add(item.Path);
            }

            _anchor = item.Path;
        }
        else
        {
            _selected.Clear();
            _selected.Add(item.Path);
            _anchor = item.Path;
        }

        OnChanged();
    }

    /// <summary>Rechtsklick auf einen nicht gewählten Eintrag wählt nur ihn; auf einen gewählten bleibt die Auswahl (wie im Explorer).</summary>
    public void SelectForContextMenu(DriveItem item)
    {
        if (!_selected.Contains(item.Path))
        {
            Select(item);
        }
    }

    public void SelectAll()
    {
        _selected.UnionWith(_items.Select(i => i.Path));
        OnChanged();
    }

    public void ClearSelection()
    {
        _selected.Clear();
        _anchor = null;
        OnChanged();
    }


    /// <summary>Neuer Ordner im aktuellen Ordner; false mit Meldung, wenn der Name ungültig oder belegt ist (der Dialog bleibt dann offen).</summary>
    public async Task<bool> CreateFolderAsync(string name, CancellationToken ct)
    {
        if (ChildPath(name) is not { } path)
        {
            return Fail(Texts.InvalidName);
        }

        if (IsTaken(path))
        {
            return Fail(Texts.NameTaken); // der Server nimmt ein vorhandenes mkdir hin (für den Sync), der Nutzer soll es erfahren
        }

        var ok = await RunAsync(() => api.CreateFolderAsync(Space!.Id, path, ct));
        await LoadAsync(ct);
        if (ok)
        {
            Select(_items.First(i => i.Path == path));
        }

        return ok;
    }

    public async Task<bool> RenameAsync(DriveItem item, string newName, CancellationToken ct)
    {
        if (newName.Trim() == item.Name)
        {
            return true;
        }

        if (ChildPath(newName) is not { } path)
        {
            return Fail(Texts.InvalidName);
        }

        if (IsTaken(path) && !string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(Texts.NameTaken);
        }

        var ok = await RunAsync(() => api.MoveAsync(Space!.Id, item, path, ct));
        await LoadAsync(ct);
        if (ok && _items.FirstOrDefault(i => i.Path == path) is { } renamed)
        {
            Select(renamed);
        }

        return ok;
    }

    /// <summary>Alle gewählten Einträge in den Papierkorb; die Oberfläche fragt vorher nach (<see cref="DeleteQuestion"/>).</summary>
    public async Task DeleteSelectedAsync(CancellationToken ct)
    {
        foreach (var item in SelectedItems)
        {
            if (!await RunAsync(() => api.DeleteAsync(Space!.Id, item, ct)))
            {
                break;
            }
        }

        _selected.Clear();
        await LoadAsync(ct);
    }

    public string DeleteQuestion => string.Format(CultureInfo.CurrentCulture, Texts.ConfirmDelete, _selected.Count);

    /// <summary>Lädt Dateien nacheinander in den aktuellen Ordner hoch; gleichnamige werden zur neuen Version der vorhandenen Datei.</summary>
    public async Task UploadAsync(IReadOnlyList<UploadFile> files, CancellationToken ct)
    {
        if (Space is null || !CanWrite)
        {
            Fail(Texts.NoPermission);
            return;
        }

        var space = Space;
        var folder = Path;
        foreach (var file in files)
        {
            if (ChildPath(file.Name) is not { } path)
            {
                Fail(Texts.InvalidName);
                continue;
            }

            var existing = _items.FirstOrDefault(i => !i.IsFolder && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            await UploadOneAsync(space, path, file, existing, ct);
            if (Space == space && Path == folder)
            {
                await LoadAsync(ct);
            }
        }
    }

    /// <summary>
    /// „Ordner hochladen“: Namen sind Pfade relativ zum aktuellen Ordner („Fotos/2026/a.jpg“). Erst entstehen alle Ordner (von oben nach
    /// unten; vorhandene nimmt der Server hin), dann kommen die Dateien.
    /// </summary>
    public async Task UploadFolderAsync(IReadOnlyList<UploadFile> files, CancellationToken ct)
    {
        if (Space is null || !CanWrite)
        {
            Fail(Texts.NoPermission);
            return;
        }

        var space = Space;
        var folder = Path;
        var targets = new List<(UploadFile File, string Path)>();
        foreach (var file in files)
        {
            var relative = file.Name.Replace('\\', '/').Trim('/');
            if (relative.Length == 0 || !RelativePath.TryParse(folder.Length == 0 ? relative : folder + "/" + relative, out var parsed))
            {
                Fail(Texts.InvalidName);
                continue;
            }

            targets.Add((file, parsed.Value));
        }

        var folders = targets
            .SelectMany(t => Ancestors(t.Path, folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Count(c => c == '/'))
            .ToList();
        foreach (var path in folders)
        {
            await RunAsync(() => api.CreateFolderAsync(space.Id, path, ct));
        }

        foreach (var (file, path) in targets)
        {
            await UploadOneAsync(space, path, file, existing: null, ct);
        }

        if (Space == space && Path == folder)
        {
            await LoadAsync(ct);
        }
    }

    /// <summary>Die Ordner zwischen <paramref name="root"/> und der Datei, ohne <paramref name="root"/> selbst.</summary>
    private static IEnumerable<string> Ancestors(string path, string root)
    {
        for (var slash = path.LastIndexOf('/'); slash > root.Length; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
        }
    }

    private async Task UploadOneAsync(SpaceSummary space, string path, UploadFile file, DriveItem? existing, CancellationToken ct)
    {
        var entry = new UploadProgress(file.Name, file.Size, 0);
        _uploads.Add(entry);
        OnChanged();
        var progress = new ImmediateProgress(sent =>
        {
            var index = _uploads.IndexOf(entry);
            if (index >= 0)
            {
                _uploads[index] = entry = entry with { Sent = sent };
                OnChanged();
            }
        });
        await RunAsync(async () =>
        {
            await using var content = file.Open();
            await api.UploadAsync(space.Id, path, content, file.Modified, existing, progress, ct);
        });
        _uploads.Remove(entry);
    }

    /// <summary>
    /// Hält die Liste aktuell, solange die Seite offen ist: wartet per Long-Poll auf Änderungen im Space und lädt den Ordner dann neu.
    /// Endet mit <paramref name="ct"/>; Fehler (kurz offline) überbrückt eine Pause.
    /// </summary>
    public async Task RunLiveUpdatesAsync(CancellationToken ct)
    {
        string? space = null;
        long cursor = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Space is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                if (Space.Id != space)
                {
                    space = Space.Id;
                    cursor = await api.CursorAsync(space, ct);
                }

                var next = await api.WaitForChangesAsync(space, cursor, ct);
                if (next != cursor && Space?.Id == space)
                {
                    cursor = next;
                    await LoadAsync(ct);
                }
            }
            catch (DriveException e) when (e.Code == ErrorCode.Unauthorized)
            {
                OnSignedOut();
                return;
            }
            catch (Exception e) when (e is DriveException or HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private string? ChildPath(string name)
    {
        var trimmed = name.Trim();
        var candidate = Path.Length == 0 ? trimmed : Path + "/" + trimmed;
        return trimmed.Length > 0 && !trimmed.Contains('/') && !trimmed.Contains('\\') && RelativePath.TryParse(candidate, out var parsed)
            ? parsed.Value
            : null;
    }

    /// <summary>Namen sind wie unter Windows ohne Rücksicht auf Groß- und Kleinschreibung eindeutig.</summary>
    private bool IsTaken(string path) => _items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    private int Compare(DriveItem a, DriveItem b)
    {
        var result = Sort switch
        {
            SortColumn.Modified => a.Modified.CompareTo(b.Modified),
            SortColumn.Size => a.Size.CompareTo(b.Size),
            _ => 0,
        };
        if (result == 0)
        {
            result = CultureInfo.CurrentCulture.CompareInfo.Compare(a.Name, b.Name, CompareOptions.IgnoreCase);
        }

        return Descending ? -result : result;
    }

    private static int IndexOf(IReadOnlyList<DriveItem> items, DriveItem item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Path == item.Path)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Meldet Fortschritt sofort (ohne SynchronizationContext), damit Tests und Oberfläche dieselbe Reihenfolge sehen.</summary>
internal sealed class ImmediateProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
