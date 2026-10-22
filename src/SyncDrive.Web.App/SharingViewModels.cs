using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// Ein Link im Freigabedialog (öffentlicher Link oder Dateianfrage) schrittweise: erst „Link erstellen“, dann Passwort und
/// Ablaufdatum (Vorgabe morgen). Ein leeres Passwortfeld behält ein vorhandenes Passwort.
/// </summary>
public sealed class LinkEditor(string kind)
{
    public string Kind { get; } = kind;

    public LinkInfo? Link { get; private set; }

    public bool Enabled { get; set; }

    public bool UsePassword { get; set; }

    public string Password { get; set; } = string.Empty;

    public bool UseExpiry { get; set; }

    public DateTime ExpiryDate { get; set; }

    /// <summary>Ende des gewählten Tages in der Zeitzone des Browsers.</summary>
    public DateTimeOffset? ExpiresAt => UseExpiry
        ? new DateTimeOffset(DateTime.SpecifyKind(ExpiryDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified), LocalOffset(ExpiryDate))
        : null;

    /// <summary>Die Richtlinie schreibt ein Passwort vor; das Kästchen ist dann fest angehakt.</summary>
    public bool PasswordRequired { get; private set; }

    /// <summary>Höchstdauer der Richtlinie in Tagen; das Ablaufdatum ist dann Pflicht und höchstens <see cref="LatestDate"/>.</summary>
    public int? MaxDays { get; private set; }

    public DateTime? LatestDate { get; private set; }

    public void Load(LinkInfo? link, DateTimeOffset now, bool passwordRequired = false, int? maxDays = null)
    {
        Link = link;
        Enabled = link is not null;
        UsePassword = passwordRequired || (link?.HasPassword ?? false);
        Password = string.Empty;
        UseExpiry = maxDays is not null || link?.ExpiresAt is not null;
        ExpiryDate = link?.ExpiresAt?.ToLocalTime().Date ?? now.ToLocalTime().Date.AddDays(1);
        PasswordRequired = passwordRequired;
        MaxDays = maxDays;
        LatestDate = maxDays is { } days ? now.ToLocalTime().Date.AddDays(days) : null;
        if (LatestDate is { } latest && ExpiryDate > latest)
        {
            ExpiryDate = latest;
        }
    }

    private static TimeSpan LocalOffset(DateTime date) => TimeZoneInfo.Local.GetUtcOffset(date.Date.AddDays(1).AddTicks(-1));
}

/// <summary>
/// Freigabedialog: Reiter „Permissions“ (Eingeladene mit Rolle, Standard Viewer) und „Public Link“ (Link mit Passwort und
/// Ablauf), dazu die Dateianfrage („Request files“) für Ordner. Eingeladen werden nur Ordner; Dateien teilt man per Link.
/// </summary>
public sealed class ShareViewModel(IDriveApi api, WebTexts texts, TimeProvider clock) : ViewModel(texts)
{
    public const string DefaultRole = "viewer";

    public static IReadOnlyList<string> Roles { get; } = ["previewer", "viewer", "editor"];

    public SpaceSummary? Space { get; private set; }

    public DriveItem? Item { get; private set; }

    public ListState State { get; private set; } = ListState.Loading;

    public bool IsBusy { get; private set; }

    public IReadOnlyList<InviteeInfo> Invitees { get; private set; } = [];

    public LinkEditor ShareLink { get; } = new(LinkKinds.Share);

    public LinkEditor RequestLink { get; } = new(LinkKinds.Request);

    /// <summary>Freigabe-Richtlinien des Servers; der Dialog blendet abgeschaltete Linkarten aus.</summary>
    public SharingPolicies Policies { get; private set; } = new();

    public bool IsFolder => Item?.IsFolder == true;

    public string Title => string.Format(CultureInfo.CurrentCulture, Texts.ShareTitle, Item?.Name);

    public string RequestTitle => string.Format(CultureInfo.CurrentCulture, Texts.RequestTitle, Item?.Name);

    public string RoleName(string role) => Texts.RoleName(role);

    /// <summary>Adresse eines Links für Empfänger: <c>{Basis}/s/{token}</c>.</summary>
    public static string UrlOf(LinkInfo link, string baseUri) => baseUri.TrimEnd('/') + "/s/" + link.Token;

    public async Task OpenAsync(SpaceSummary space, DriveItem item, CancellationToken ct)
    {
        (Space, Item) = (space, item);
        DismissMessage();
        await LoadAsync(ct);
    }

    public async Task<bool> InviteAsync(string userName, string role, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return Fail(Texts.UnknownUser);
        }

        return await ChangeAsync(() => api.InviteAsync(Space!.Id, Item!.Path, userName.Trim(), role, ct), ct);
    }

    public Task<bool> ChangeRoleAsync(InviteeInfo invitee, string role, CancellationToken ct) =>
        ChangeAsync(() => api.InviteAsync(Space!.Id, Item!.Path, invitee.UserName, role, ct), ct);

    public Task<bool> RemoveAsync(InviteeInfo invitee, CancellationToken ct) => ChangeAsync(() => api.RemoveInviteAsync(invitee.ShareId, ct), ct);

    /// <summary>Speichert einen Link: anlegen, ändern oder (ausgeschaltet) entfernen.</summary>
    public async Task<bool> SaveLinkAsync(LinkEditor editor, CancellationToken ct)
    {
        if (editor.Enabled && editor.UsePassword && editor.Password.Length == 0 && editor.Link?.HasPassword != true)
        {
            return Fail(Texts.PasswordMissing);
        }

        if (editor.Enabled && editor.UsePassword && editor.Password.Length is > 0 and < 4)
        {
            return Fail(Texts.PasswordTooShort);
        }

        if (editor.Enabled && editor.LatestDate is { } latest && (!editor.UseExpiry || editor.ExpiryDate.Date > latest))
        {
            return Fail(string.Format(CultureInfo.CurrentCulture, Texts.ExpiryTooLate, editor.MaxDays));
        }

        var ok = await ChangeAsync(async () =>
        {
            if (editor.Enabled)
            {
                await api.SetLinkAsync(Space!.Id, Item!.Path, editor.Kind, new LinkRequest(
                    editor.ExpiresAt,
                    editor.UsePassword && editor.Password.Length > 0 ? editor.Password : null,
                    RemovePassword: !editor.UsePassword && editor.Link?.HasPassword == true), ct);
            }
            else if (editor.Link is { } link)
            {
                await api.RemoveLinkAsync(link.Id, ct);
            }
        }, ct);
        if (ok)
        {
            Inform(Texts.Saved);
        }

        return ok;
    }

    /// <summary>Nach „Get link“: kopiert, oder (wenn der Browser die Zwischenablage verweigert) der Link zum Abschreiben.</summary>
    public void ShowLinkCopied(string url, bool copied) =>
        Inform(copied ? string.Format(CultureInfo.CurrentCulture, Texts.LinkCopied, url) : url);

    /// <summary>„Get link“: der vorhandene öffentliche Link oder ein neuer ohne Passwort und Ablauf.</summary>
    public async Task<LinkInfo?> EnsureLinkAsync(SpaceSummary space, DriveItem item, CancellationToken ct)
    {
        LinkInfo? link = null;
        await RunAsync(async () => link = (await api.GetSharingAsync(space.Id, item.Path, ct)).Link
            ?? await api.SetLinkAsync(space.Id, item.Path, LinkKinds.Share, new LinkRequest(null), ct), Explain);
        return link;
    }

    private async Task<bool> ChangeAsync(Func<Task> change, CancellationToken ct)
    {
        IsBusy = true;
        OnChanged();
        var ok = await RunAsync(change, Explain);
        IsBusy = false;
        var message = Message;
        await LoadAsync(ct);
        if (message is not null)
        {
            Fail(message); // der Fehler bleibt sichtbar, auch nach dem Neuladen
        }

        return ok;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        SharingResponse? sharing = null;
        await RunAsync(async () => sharing = await api.GetSharingAsync(Space!.Id, Item!.Path, ct), Explain);
        await RunAsync(async () => Policies = await api.GetPoliciesAsync(ct), Explain);
        Invitees = sharing?.Invitees ?? [];
        ShareLink.Load(sharing?.Link, clock.GetUtcNow(), Policies.LinkPasswordRequired, Policies.LinkMaxDays);
        RequestLink.Load(sharing?.Request, clock.GetUtcNow(), Policies.RequestPasswordRequired, Policies.RequestMaxDays);
        State = sharing is null ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    private string? Explain(DriveException e) => e.Code switch
    {
        ErrorCode.AlreadyExists => Texts.AlreadyHasAccess,
        ErrorCode.NotFound => Texts.UnknownUser,
        ErrorCode.Forbidden => Texts.NoSharePermission,
        ErrorCode.InvalidRequest => string.Format(CultureInfo.CurrentCulture, Texts.GenericError, e.Message),
        _ => null,
    };
}

/// <summary>Ein Element in „Shared with others“ oder „File Request“ mit seinem Space.</summary>
public sealed record SharedRow(SpaceSummary Space, SharedItem Shared)
{
    public LocatedItem Located =>
        new(Space, new DriveItem(Shared.Path, Shared.Kind == NodeKinds.Directory, 0, default, 0, Shared.NodeId));
}

/// <summary>„Shared with others“ (was der Benutzer geteilt hat: Personen, Link, Dateianfrage) und „File Request“ (seine Dateianfragen).</summary>
public sealed class SharedByMeViewModel(IDriveApi api, WebTexts texts, TimeProvider clock) : ViewModel(texts)
{
    public ListState State { get; private set; } = ListState.Loading;

    public IReadOnlyList<SharedRow> Rows { get; private set; } = [];

    public IReadOnlyList<SharedRow> Requests => [.. Rows.Where(r => r.Shared.Request is not null)];

    public async Task LoadAsync(IReadOnlyList<SpaceSummary> spaces, CancellationToken ct)
    {
        State = ListState.Loading;
        OnChanged();
        IReadOnlyList<SharedItem> items = [];
        await RunAsync(async () => items = await api.SharedByMeAsync(ct));
        Rows = [.. items
            .Select(i => (Space: spaces.FirstOrDefault(s => s.Id == i.SpaceId), Item: i))
            .Where(p => p.Space is not null)
            .Select(p => new SharedRow(p.Space!, p.Item))];
        State = Rows.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    /// <summary>„2 Personen · Link · Dateianfrage“.</summary>
    public string Summary(SharedRow row)
    {
        var parts = new List<string>();
        if (row.Shared.Invitees.Count > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, Texts.PeopleCount, row.Shared.Invitees.Count));
        }

        if (row.Shared.Link is not null)
        {
            parts.Add(Texts.PublicLink);
        }

        if (row.Shared.Request is not null)
        {
            parts.Add(Texts.FileRequest);
        }

        return string.Join(" · ", parts);
    }

    public bool IsExpired(LinkInfo link) => link.ExpiresAt is { } expires && expires <= clock.GetUtcNow();

    public string StatusOf(LinkInfo link) => IsExpired(link) ? Texts.Expired : Texts.Active;
}

/// <summary>
/// Die Seite eines öffentlichen Links (<c>/s/{token}</c>) ohne Anmeldung: Passwort abfragen, Ordner durchblättern und herunterladen
/// (einzeln oder alles als ZIP), eine Datei herunterladen, bei einer Dateianfrage Dateien hochladen.
/// </summary>
public sealed class PublicLinkViewModel(IPublicLinkApi api, WebTexts texts) : ViewModel(texts)
{
    private readonly List<UploadProgress> _uploads = [];
    private readonly List<string> _uploaded = [];

    public string Token { get; private set; } = string.Empty;

    public PublicLinkInfo? Info { get; private set; }

    /// <summary>Abgelaufen oder unbekannt: die Seite zeigt nur diesen Satz.</summary>
    public string? Problem { get; private set; }

    public bool IsLoading { get; private set; } = true;

    public bool NeedsPassword => Info?.PasswordRequired == true;

    public bool IsRequest => Info?.Kind == LinkKinds.Request;

    public bool IsFolder => Info?.ItemKind == NodeKinds.Directory;

    public string Path { get; private set; } = string.Empty;

    public IReadOnlyList<DriveItem> Items { get; private set; } = [];

    public IReadOnlyList<UploadProgress> Uploads => _uploads;

    /// <summary>Namen, unter denen hochgeladene Dateien abgelegt wurden.</summary>
    public IReadOnlyList<string> Uploaded => _uploaded;

    public IReadOnlyList<Crumb> Breadcrumbs
    {
        get
        {
            var crumbs = new List<Crumb> { new(Info?.Name ?? string.Empty, string.Empty) };
            var parts = Path.Length == 0 ? [] : Path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                crumbs.Add(new Crumb(parts[i], string.Join('/', parts[..(i + 1)])));
            }

            return crumbs;
        }
    }

    public string RequestHint => string.Format(CultureInfo.CurrentCulture, Texts.RequestPageHint, Info?.Owner ?? "?", Info?.Name);

    public string? ExpiresText => Info?.ExpiresAt is { } expires
        ? string.Format(CultureInfo.CurrentCulture, Texts.LinkExpiresOn, expires.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
        : null;

    public async Task LoadAsync(string token, CancellationToken ct)
    {
        Token = token;
        IsLoading = true;
        OnChanged();
        try
        {
            Info = await api.GetLinkAsync(token, ct);
            if (!NeedsPassword)
            {
                await ListAsync(ct);
            }
        }
        catch (DriveException e)
        {
            Problem = e.Code == ErrorCode.LinkExpired ? Texts.LinkExpired : Texts.LinkNotFound;
        }
        catch (HttpRequestException e)
        {
            Problem = string.Format(CultureInfo.CurrentCulture, Texts.GenericError, e.Message);
        }

        IsLoading = false;
        OnChanged();
    }

    public async Task<bool> UnlockAsync(string password, CancellationToken ct)
    {
        try
        {
            Info = await api.UnlockLinkAsync(Token, password, ct);
            DismissMessage();
            await ListAsync(ct);
            return true;
        }
        catch (DriveException e) when (e.Code == ErrorCode.PasswordRequired)
        {
            return Fail(Texts.WrongPassword);
        }
        catch (DriveException e)
        {
            return Fail(Texts.For(e));
        }
    }

    public async Task NavigateAsync(string path, CancellationToken ct)
    {
        Path = path;
        await ListAsync(ct);
    }

    public string DownloadUrl(DriveItem? item) => api.LinkDownloadUrl(Token, item?.Path);

    public string ZipUrl(DriveItem? folder = null) => api.LinkZipUrl(Token, folder is null ? [] : [folder.Path]);

    public string ZipName(DriveItem? folder = null) => (folder?.Name ?? Info?.Name) + ".zip";

    /// <summary>Lädt Dateien nacheinander in die Dateianfrage; der Server vergibt bei gleichem Namen einen neuen („Bericht (2).pdf“).</summary>
    public async Task UploadAsync(IReadOnlyList<UploadFile> files, CancellationToken ct)
    {
        foreach (var file in files)
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
                _uploaded.Add((await api.UploadToLinkAsync(Token, file.Name, content, progress, ct)).Name);
            });
            _uploads.Remove(entry);
            OnChanged();
        }
    }

    private async Task ListAsync(CancellationToken ct)
    {
        if (IsRequest || !IsFolder)
        {
            return;
        }

        var path = Path;
        await RunAsync(async () =>
        {
            var items = await api.ListLinkAsync(Token, path, ct);
            if (path == Path)
            {
                Items = [.. items.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        });
        OnChanged();
    }
}
