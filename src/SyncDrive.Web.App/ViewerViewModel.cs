using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>Wie eine Datei in der Oberfläche angezeigt wird; <see cref="None"/> heißt herunterladen.</summary>
public enum ViewerKind
{
    None,
    Image,
    Pdf,
    Text,
    Markdown,
    Rtf,
    Docx,
}

/// <summary>
/// Doppelklick auf eine Datei: Bilder groß, PDF im eingebauten Betrachter des Browsers, Text, Markdown, RTF und Word-Dokumente lesbar
/// in der Oberfläche. Größere Textdateien als <see cref="MaxReadBytes"/> lädt man herunter.
/// </summary>
public sealed class ViewerViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const long MaxReadBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml", ".env", ".sql",
        ".cs", ".js", ".ts", ".py", ".java", ".c", ".h", ".cpp", ".go", ".rs", ".php", ".rb", ".sh", ".ps1", ".bat", ".css", ".html", ".htm",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

    public static ViewerKind KindOf(DriveItem item) => item.IsFolder ? ViewerKind.None : Path.GetExtension(item.Name).ToLowerInvariant() switch
    {
        var e when ImageExtensions.Contains(e) => ViewerKind.Image,
        ".pdf" => ViewerKind.Pdf,
        ".md" or ".markdown" => ViewerKind.Markdown,
        ".rtf" => ViewerKind.Rtf,
        ".docx" => ViewerKind.Docx,
        var e when TextExtensions.Contains(e) => ViewerKind.Text,
        _ => ViewerKind.None,
    };

    public DriveItem? Item { get; private set; }

    public ViewerKind Kind { get; private set; }

    public ListState State { get; private set; } = ListState.Empty;

    /// <summary>Adresse des PDF für den eingebauten Betrachter.</summary>
    public string? PdfUrl { get; private set; }

    /// <summary>Text (auch aus RTF), als Text zu zeigen.</summary>
    public string? Text { get; private set; }

    /// <summary>Aus Markdown oder DOCX erzeugtes, gefiltertes HTML (<see cref="DocumentViews"/>).</summary>
    public string? Html { get; private set; }

    /// <summary>Die Datei ist zu groß oder nicht lesbar: nur Herunterladen anbieten.</summary>
    public string? Problem { get; private set; }

    public string DownloadUrl(SpaceSummary space) => api.DownloadUrl(space.Id, Item!.Path);

    public async Task OpenAsync(SpaceSummary space, DriveItem item, CancellationToken ct)
    {
        (Item, Kind, PdfUrl, Text, Html, Problem) = (item, KindOf(item), null, null, null, null);
        if (Kind == ViewerKind.Pdf)
        {
            PdfUrl = api.ViewUrl(space.Id, item.Path, item.Revision);
            State = ListState.Ready;
            OnChanged();
            return;
        }

        if (Kind is ViewerKind.None or ViewerKind.Image)
        {
            State = ListState.Ready;
            OnChanged();
            return;
        }

        if (item.Size > MaxReadBytes)
        {
            Problem = Texts.TooLargeToView;
            State = ListState.Ready;
            OnChanged();
            return;
        }

        State = ListState.Loading;
        OnChanged();
        byte[] bytes = [];
        if (await RunAsync(async () => bytes = await api.ReadFileAsync(space.Id, item.Path, ct)) && Item == item)
        {
            switch (Kind)
            {
                case ViewerKind.Markdown:
                    Html = DocumentViews.MarkdownToHtml(DocumentViews.DecodeText(bytes));
                    break;
                case ViewerKind.Docx:
                    Html = DocumentViews.DocxToHtml(bytes);
                    Problem = Html is null ? Texts.CannotView : null;
                    break;
                case ViewerKind.Rtf:
                    Text = DocumentViews.RtfToText(DocumentViews.DecodeText(bytes));
                    break;
                default:
                    Text = DocumentViews.DecodeText(bytes);
                    break;
            }
        }

        State = ListState.Ready;
        OnChanged();
    }

    public void Close()
    {
        (Item, Kind, PdfUrl, Text, Html, Problem, State) = (null, ViewerKind.None, null, null, null, null, ListState.Empty);
        OnChanged();
    }
}
