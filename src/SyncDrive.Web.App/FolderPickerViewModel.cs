using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// „Kopieren nach …“ / „Verschieben nach …“: Zielordner im selben Space wählen (Pfadleiste, Unterordner betreten) und die Auswahl
/// dorthin kopieren oder verschieben. Ein Ordner kann nicht in sich selbst landen; was am Ziel schon gleich heißt, wird gemeldet.
/// </summary>
public sealed class FolderPickerViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    private IReadOnlyList<DriveItem> _items = [];
    private string _rootLabel = string.Empty;

    public SpaceSummary? Space { get; private set; }

    public bool IsMove { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public IReadOnlyList<DriveItem> Folders { get; private set; } = [];

    public ListState State { get; private set; } = ListState.Empty;

    public bool IsOpen => Space is not null;

    /// <summary>Pfadleiste: Wurzel (Name des Space) und jeder Ordner bis zum aktuellen.</summary>
    public IReadOnlyList<(string Label, string Path)> Crumbs
    {
        get
        {
            var crumbs = new List<(string, string)> { (_rootLabel, string.Empty) };
            var path = string.Empty;
            foreach (var part in Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? part : path + "/" + part;
                crumbs.Add((part, path));
            }

            return crumbs;
        }
    }

    /// <summary>Hier ablegen geht nicht in einen der gewählten Ordner hinein und beim Verschieben nicht an den alten Ort.</summary>
    public bool CanPlaceHere => IsOpen && !_items.Any(i => i.IsFolder && IsSameOrInside(Path, i.Path))
        && !(IsMove && _items.All(i => ParentOf(i.Path) == Path));

    /// <summary>Gewählte Ordner selbst erscheinen nicht als Ziel.</summary>
    public bool IsHidden(DriveItem folder) => _items.Any(i => i.Path == folder.Path);

    /// <param name="rootLabel">Name der Wurzel in der Pfadleiste, wie die Seitenleiste ihn zeigt (z. B. „My Drive“).</param>
    public async Task OpenAsync(
        SpaceSummary space, string startPath, IReadOnlyList<DriveItem> items, bool move, string? rootLabel, CancellationToken ct)
    {
        (Space, _items, IsMove, _rootLabel) = (space, items, move, rootLabel ?? space.Name);
        await NavigateAsync(startPath, ct);
    }

    public async Task NavigateAsync(string path, CancellationToken ct)
    {
        Path = path;
        State = ListState.Loading;
        OnChanged();
        IReadOnlyList<DriveItem> children = [];
        await RunAsync(async () => children = await api.ListAsync(Space!.Id, path, ct));
        Folders = [.. children.Where(c => c.IsFolder).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
        State = Folders.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    /// <summary>Kopiert oder verschiebt alle gewählten Elemente; Fehler (z. B. Name schon vorhanden) bleiben als Meldung stehen.</summary>
    public async Task<bool> ConfirmAsync(CancellationToken ct)
    {
        if (!CanPlaceHere)
        {
            return Fail(Texts.CannotPlaceInside);
        }

        var done = 0;
        foreach (var item in _items)
        {
            var target = Path.Length == 0 ? item.Name : Path + "/" + item.Name;
            if (await RunAsync(() => IsMove ? api.MoveAsync(Space!.Id, item, target, ct) : api.CopyAsync(Space!.Id, item, target, ct), Explain))
            {
                done++;
            }
            else
            {
                break;
            }
        }

        if (done == _items.Count)
        {
            var message = string.Format(CultureInfo.CurrentCulture, IsMove ? Texts.Moved : Texts.Copied, done);
            Close();
            Inform(message);
            return true;
        }

        OnChanged();
        return false;
    }

    public void Close()
    {
        DismissMessage();
        (Space, _items, Folders, Path) = (null, [], [], string.Empty);
        OnChanged();
    }

    private string? Explain(DriveException e) => e.Code == ErrorCode.AlreadyExists ? Texts.NameTaken : null;

    private static string ParentOf(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
}
