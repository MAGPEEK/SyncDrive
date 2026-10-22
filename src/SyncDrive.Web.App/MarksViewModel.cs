using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// Favoriten und Labels: Stern in jeder Zeile und im Kontextmenü, „Starred“ als Kategorie, eigene Labels mit Farbe unter
/// „My Labels“. Beides hängt an der Knoten-Id und übersteht Umbenennen und Verschieben. Für eine Auswahl wirkt beides als Umschalter:
/// fehlt es einem Element, bekommen es alle; haben es alle, verlieren es alle.
/// </summary>
public sealed class MarksViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const int MaxLabelName = 64;

    /// <summary>Farben zur Wahl beim Anlegen eines Labels.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["#d64545", "#e8833a", "#d9b310", "#3c9d5d", "#1f9e9a", "#3b7dd8", "#8b5cc7", "#7f8c8d"];

    private IReadOnlyList<ItemRef> _starred = [];
    private Dictionary<string, IReadOnlyList<ItemRef>> _labeled = [];

    public IReadOnlyList<LabelInfo> Labels { get; private set; } = [];

    public ListState State { get; private set; } = ListState.Loading;

    public async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync(async () =>
        {
            var starred = await api.ListStarsAsync(ct);
            var labels = await api.ListLabelsAsync(ct);
            var labeled = new Dictionary<string, IReadOnlyList<ItemRef>>(StringComparer.Ordinal);
            foreach (var label in labels)
            {
                labeled[label.Id] = await api.ListLabeledAsync(label.Id, ct);
            }

            (_starred, Labels, _labeled) = (starred, [.. labels.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)], labeled);
        });
        State = ListState.Ready;
        OnChanged();
    }

    public bool IsStarred(SpaceSummary space, DriveItem item) => Contains(_starred, space, item);

    public bool Has(LabelInfo label, SpaceSummary space, DriveItem item) =>
        _labeled.TryGetValue(label.Id, out var items) && Contains(items, space, item);

    public IReadOnlyList<LabelInfo> LabelsOf(SpaceSummary space, DriveItem item) => [.. Labels.Where(l => Has(l, space, item))];

    public bool AllStarred(IReadOnlyList<LocatedItem> items) => items.Count > 0 && items.All(i => IsStarred(i.Space, i.Item));

    public bool AllHave(LabelInfo label, IReadOnlyList<LocatedItem> items) => items.Count > 0 && items.All(i => Has(label, i.Space, i.Item));

    /// <summary>„Add star“ / „Remove star“ für die Auswahl.</summary>
    public async Task ToggleStarAsync(IReadOnlyList<LocatedItem> items, CancellationToken ct)
    {
        var star = !AllStarred(items);
        foreach (var entry in items.Where(i => IsStarred(i.Space, i.Item) != star))
        {
            if (!await RunAsync(() => api.SetStarAsync(entry.Space.Id, entry.Item.Path, star, ct)))
            {
                break;
            }
        }

        await LoadAsync(ct);
    }

    /// <summary>Ein Label im Untermenü „Labels“ an- oder abhaken.</summary>
    public async Task ToggleLabelAsync(LabelInfo label, IReadOnlyList<LocatedItem> items, CancellationToken ct)
    {
        var assign = !AllHave(label, items);
        foreach (var entry in items.Where(i => Has(label, i.Space, i.Item) != assign))
        {
            if (!await RunAsync(() => api.SetLabelAsync(label.Id, entry.Space.Id, entry.Item.Path, assign, ct)))
            {
                break;
            }
        }

        await LoadAsync(ct);
    }

    /// <summary>Kategorie „Starred“: Favoriten aller Spaces, die man noch sehen darf.</summary>
    public IReadOnlyList<LocatedItem> Starred(IReadOnlyList<SpaceSummary> spaces) => Located(_starred, spaces);

    /// <summary>Ein Label unter „My Labels“ zeigt alles, was es trägt.</summary>
    public IReadOnlyList<LocatedItem> ItemsOf(LabelInfo label, IReadOnlyList<SpaceSummary> spaces) =>
        Located(_labeled.GetValueOrDefault(label.Id) ?? [], spaces);

    /// <summary>
    /// Neues Label; false mit Meldung, wenn der Name fehlt, zu lang oder schon vergeben ist (der Dialog bleibt dann offen). Aus dem Untermenü
    /// „Labels“ heraus bekommen die gewählten Elemente (<paramref name="assignTo"/>) das neue Label gleich mit.
    /// </summary>
    public async Task<bool> CreateLabelAsync(string name, string color, IReadOnlyList<LocatedItem> assignTo, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxLabelName)
        {
            return Fail(Texts.InvalidLabelName);
        }

        if (Labels.Any(l => string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail(Texts.LabelExists);
        }

        var ok = await RunAsync(async () =>
        {
            var label = await api.CreateLabelAsync(trimmed, color, ct);
            foreach (var entry in assignTo)
            {
                await api.SetLabelAsync(label.Id, entry.Space.Id, entry.Item.Path, assigned: true, ct);
            }
        });
        await LoadAsync(ct);
        return ok;
    }

    /// <summary>Entfernt das Label von allen Elementen; die Elemente selbst bleiben unberührt.</summary>
    public async Task<bool> DeleteLabelAsync(LabelInfo label, CancellationToken ct)
    {
        var ok = await RunAsync(() => api.DeleteLabelAsync(label.Id, ct));
        await LoadAsync(ct);
        return ok;
    }

    public string DeleteLabelQuestion(LabelInfo label) => string.Format(CultureInfo.CurrentCulture, Texts.ConfirmDeleteLabel, label.Name);

    private static bool Contains(IReadOnlyList<ItemRef> items, SpaceSummary space, DriveItem item) =>
        item.NodeId is { } id && items.Any(i => i.SpaceId == space.Id && i.NodeId == id);

    private static IReadOnlyList<LocatedItem> Located(IReadOnlyList<ItemRef> items, IReadOnlyList<SpaceSummary> spaces) =>
    [
        .. items
            .Select(i => (Space: spaces.FirstOrDefault(s => s.Id == i.SpaceId), Ref: i))
            .Where(p => p.Space is not null)
            .Select(p => new LocatedItem(
                p.Space!, new DriveItem(p.Ref.Path, p.Ref.Kind == NodeKinds.Directory, p.Ref.Size, p.Ref.MTime, p.Ref.Revision, p.Ref.NodeId)))
            .OrderBy(e => e.Item.IsFolder ? 0 : 1)
            .ThenBy(e => e.Item.Name, StringComparer.CurrentCultureIgnoreCase),
    ];
}
