namespace SyncDrive.Web.App;

/// <summary>
/// Ordnerbaum der Seitenleiste unter „Meine Dateien“ und je Team-Ordner: Ebenen werden erst beim Aufklappen geladen und dann gemerkt;
/// „Team-Ordner“ selbst ist eine aufklappbare Gruppe. Gezeigt werden nur Ordner, nach Namen sortiert wie im Explorer.
/// </summary>
public sealed class FolderTreeViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    private readonly Dictionary<(string Space, string Path), IReadOnlyList<DriveItem>> _children = [];
    private readonly HashSet<(string Space, string Path)> _expanded = [];

    public bool TeamFoldersExpanded { get; private set; } = true;

    public bool IsExpanded(string spaceId, string path) => _expanded.Contains((spaceId, path));

    /// <summary>Unterordner, sobald geladen; leer, solange zugeklappt oder noch nicht geladen.</summary>
    public IReadOnlyList<DriveItem> Children(string spaceId, string path) =>
        IsExpanded(spaceId, path) && _children.TryGetValue((spaceId, path), out var children) ? children : [];

    /// <summary>Ob die Ebene schon geladen ist und keine Unterordner hat (dann zeigt die Leiste keinen Pfeil mehr).</summary>
    public bool IsLeaf(string spaceId, string path) => _children.TryGetValue((spaceId, path), out var children) && children.Count == 0;

    public async Task ToggleAsync(string spaceId, string path, CancellationToken ct)
    {
        if (!_expanded.Add((spaceId, path)))
        {
            _expanded.Remove((spaceId, path));
            OnChanged();
            return;
        }

        if (!_children.ContainsKey((spaceId, path)))
        {
            await RunAsync(async () =>
            {
                var items = await api.ListAsync(spaceId, path, ct);
                _children[(spaceId, path)] = [.. items.Where(i => i.IsFolder).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
            });
        }

        OnChanged();
    }

    public void ToggleTeamFolders()
    {
        TeamFoldersExpanded = !TeamFoldersExpanded;
        OnChanged();
    }

    /// <summary>Nach Änderungen (Live-Aktualisierung, neuer Ordner): geladene Ebenen beim nächsten Aufklappen neu holen, offene sofort.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var open = _expanded.ToList();
        _children.Clear();
        _expanded.Clear();
        foreach (var (space, path) in open)
        {
            await ToggleAsync(space, path, ct);
        }
    }
}
