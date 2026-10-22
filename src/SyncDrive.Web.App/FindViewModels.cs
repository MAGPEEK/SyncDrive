using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>Ein Eintrag mit seinem Space (Suche und „Zuletzt“ zeigen Treffer aus allen Spaces).</summary>
public sealed record LocatedItem(SpaceSummary Space, DriveItem Item)
{
    /// <summary>„Ort“-Spalte: Space und Ordner, z. B. „My Drive / Projekte“.</summary>
    public string Location(Func<SpaceSummary, string> spaceLabel) =>
        Item.Path.Contains('/') ? $"{spaceLabel(Space)} / {Item.Path[..Item.Path.LastIndexOf('/')].Replace("/", " / ", StringComparison.Ordinal)}" : spaceLabel(Space);
}

/// <summary>Suche mit Bereich: im aktuellen Ordner (samt Unterordnern) oder überall.</summary>
public sealed class SearchViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public string Query { get; private set; } = string.Empty;

    public bool Everywhere { get; private set; }

    public ListState State { get; private set; } = ListState.Empty;

    public IReadOnlyList<LocatedItem> Results { get; private set; } = [];

    /// <summary>Sucht in <paramref name="space"/> unter <paramref name="path"/> oder, mit <paramref name="everywhere"/>, in allen Spaces.</summary>
    public async Task SearchAsync(
        string query, bool everywhere, IReadOnlyList<SpaceSummary> spaces, SpaceSummary? space, string path, CancellationToken ct)
    {
        Query = query.Trim();
        Everywhere = everywhere;
        if (Query.Length == 0)
        {
            Results = [];
            State = ListState.Empty;
            OnChanged();
            return;
        }

        State = ListState.Loading;
        OnChanged();
        var scopes = everywhere ? spaces.Select(s => (s, string.Empty)) : space is null ? [] : [(space, path)];
        var results = new List<LocatedItem>();
        await RunAsync(async () =>
        {
            foreach (var (scope, root) in scopes)
            {
                results.AddRange((await api.SearchAsync(scope.Id, root, Query, ct)).Select(i => new LocatedItem(scope, i)));
            }
        });
        Results = results;
        State = results.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }
}

/// <summary>„Zuletzt“: die zuletzt geänderten Dateien aller Spaces, neueste zuerst (höchstens 50).</summary>
public sealed class RecentViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const int MaxItems = 50;

    public ListState State { get; private set; } = ListState.Loading;

    public IReadOnlyList<LocatedItem> Items { get; private set; } = [];

    public async Task LoadAsync(IReadOnlyList<SpaceSummary> spaces, CancellationToken ct)
    {
        State = ListState.Loading;
        OnChanged();
        var items = new List<LocatedItem>();
        await RunAsync(async () =>
        {
            foreach (var space in spaces)
            {
                items.AddRange((await api.RecentAsync(space.Id, ct)).Select(i => new LocatedItem(space, i)));
            }
        });
        Items = [.. items.OrderByDescending(i => i.Item.Modified).Take(MaxItems)];
        State = Items.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }
}
