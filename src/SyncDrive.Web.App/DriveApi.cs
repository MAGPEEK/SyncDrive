using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SyncDrive.Contracts;
using SyncDrive.Core.Chunking;

namespace SyncDrive.Web.App;

/// <summary>Ein Eintrag der Dateiliste.</summary>
/// <param name="Path">Pfad im Space, mit <c>/</c> getrennt.</param>
/// <param name="NodeId">Stabile Id des Knotens; Favoriten und Labels hängen an ihr und überstehen so Umbenennen und Verschieben.</param>
public sealed record DriveItem(string Path, bool IsFolder, long Size, DateTimeOffset Modified, long Revision, string? NodeId = null)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>Erwartbarer Fehler des Servers mit stabilem Code (<see cref="ErrorCode"/>), z. B. „gibt es schon“ oder „nicht angemeldet“.</summary>
public sealed class DriveException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Was die Weboberfläche vom Server braucht. Die ViewModels kennen nur diese Schnittstelle.</summary>
public interface IDriveApi
{
    /// <summary>Anmeldeseite (ohne Anmeldung lesbar): Servername, Text, Hintergrundbild.</summary>
    Task<PortalInfo> GetPortalAsync(CancellationToken ct);

    /// <summary>Anmeldeseite: Knopf „Anmelden mit …“ (ohne Anmeldung lesbar).</summary>
    Task<OAuthLoginInfo> GetOAuthLoginAsync(CancellationToken ct);

    Task<OAuthSettingsInfo> GetOAuthSettingsAsync(CancellationToken ct);

    Task<OAuthSettingsInfo> SaveOAuthSettingsAsync(OAuthSettingsRequest request, CancellationToken ct);

    /// <summary>Nur Administratoren.</summary>
    Task<PortalInfo> UpdatePortalAsync(string serverName, string? loginMessage, CancellationToken ct);

    /// <summary>Nur Administratoren; JPEG, PNG oder WebP bis 5 MB.</summary>
    Task<PortalInfo> SetPortalBackgroundAsync(Stream image, CancellationToken ct);

    Task<PortalInfo> RemovePortalBackgroundAsync(CancellationToken ct);

    /// <summary>Eigenes Profilbild ersetzen (JPEG, PNG oder WebP; der Server verkleinert es).</summary>
    Task SetAvatarAsync(Stream image, CancellationToken ct);

    Task RemoveAvatarAsync(CancellationToken ct);

    /// <summary>Angemeldet? Null, wenn kein gültiges Sitzungs-Cookie da ist.</summary>
    Task<WebSessionInfo?> GetSessionAsync(CancellationToken ct);

    Task<WebSessionInfo> SignInAsync(string userName, string password, CancellationToken ct);

    Task SignOutAsync(CancellationToken ct);

    Task<IReadOnlyList<SpaceSummary>> ListSpacesAsync(CancellationToken ct);

    /// <summary>Unmittelbarer Inhalt eines Ordners (leerer Pfad = Wurzel des Space).</summary>
    Task<IReadOnlyList<DriveItem>> ListAsync(string spaceId, string path, CancellationToken ct);

    Task CreateFolderAsync(string spaceId, string path, CancellationToken ct);

    /// <summary>Umbenennen und Verschieben; scheitert, wenn sich der Eintrag inzwischen geändert hat oder das Ziel belegt ist.</summary>
    Task MoveAsync(string spaceId, DriveItem item, string newPath, CancellationToken ct);

    /// <summary>Kopie innerhalb des Space (Ordner samt Inhalt); am Ziel darf noch nichts liegen.</summary>
    Task CopyAsync(string spaceId, DriveItem item, string newPath, CancellationToken ct);

    /// <summary>Produkt und Version des Servers (<c>GET /info</c>).</summary>
    Task<ServerInfo> GetServerInfoAsync(CancellationToken ct);

    /// <summary>In den Papierkorb (der Server hebt Gelöschtes auf).</summary>
    Task DeleteAsync(string spaceId, DriveItem item, CancellationToken ct);

    /// <summary>Lädt eine Datei hoch; <paramref name="replaces"/> ist die vorhandene gleichnamige Datei (wird zur neuen Version).</summary>
    Task UploadAsync(
        string spaceId, string path, Stream content, DateTimeOffset modified, DriveItem? replaces, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Adresse zum Herunterladen; der Browser lädt direkt (Cookie), nichts geht durch den Arbeitsspeicher der App.</summary>
    /// <param name="revision">Eine frühere Version; ohne die aktuelle Fassung.</param>
    string DownloadUrl(string spaceId, string path, long? revision = null);

    /// <summary>Adresse des Vorschaubilds; die Revision steht darin, damit der Browser-Cache eine neue Fassung nicht übersieht.</summary>
    string ThumbnailUrl(string spaceId, string path, long revision, bool large = false);

    /// <summary>PDF zum Ansehen im Browser (inline); der Server liefert nur PDF so aus.</summary>
    string ViewUrl(string spaceId, string path, long revision);

    /// <summary>Inhalt einer Datei für die Anzeige in der Oberfläche (Text, Markdown, RTF, DOCX); die Aufrufer begrenzen die Größe.</summary>
    Task<byte[]> ReadFileAsync(string spaceId, string path, CancellationToken ct);

    /// <summary>Einträge unter <paramref name="path"/>, deren Name den Begriff enthält.</summary>
    Task<IReadOnlyList<DriveItem>> SearchAsync(string spaceId, string path, string query, CancellationToken ct);

    /// <summary>Zuletzt geänderte Dateien des Space, neueste zuerst.</summary>
    Task<IReadOnlyList<DriveItem>> RecentAsync(string spaceId, CancellationToken ct);

    Task<IReadOnlyList<TrashEntry>> ListTrashAsync(string spaceId, CancellationToken ct);

    /// <summary>Stellt ein gelöschtes Element am ursprünglichen Ort wieder her.</summary>
    Task RestoreFromTrashAsync(string spaceId, string nodeId, CancellationToken ct);

    /// <summary>Gespeicherte Fassungen einer Datei, neueste zuerst.</summary>
    Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(string spaceId, string path, CancellationToken ct);

    /// <summary>Macht eine Version zur aktuellen; Ergebnis ist die neue Revision der Datei.</summary>
    Task<long> RestoreVersionAsync(string spaceId, string path, long revision, long currentRevision, CancellationToken ct);

    /// <summary>Adresse für Ordner oder mehrere Elemente eines Ordners als ZIP; der Server packt beim Senden.</summary>
    string ZipUrl(string spaceId, IReadOnlyList<string> paths);

    /// <summary>Favoriten aller Spaces mit aktuellem Pfad.</summary>
    Task<IReadOnlyList<ItemRef>> ListStarsAsync(CancellationToken ct);

    Task SetStarAsync(string spaceId, string path, bool starred, CancellationToken ct);

    Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct);

    Task<LabelInfo> CreateLabelAsync(string name, string color, CancellationToken ct);

    Task DeleteLabelAsync(string labelId, CancellationToken ct);

    Task<IReadOnlyList<ItemRef>> ListLabeledAsync(string labelId, CancellationToken ct);

    Task SetLabelAsync(string labelId, string spaceId, string path, bool assigned, CancellationToken ct);

    /// <summary>Neueste zuerst.</summary>
    Task<IReadOnlyList<NotificationInfo>> ListNotificationsAsync(CancellationToken ct);

    Task MarkNotificationReadAsync(string notificationId, CancellationToken ct);

    /// <summary>Geräte des Benutzers („Client list“), das eigene Browser-Gerät mit <c>IsCurrent</c>.</summary>
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct);

    /// <summary>„Unlink“: Das Gerät verliert seinen Zugang und muss sich neu anmelden.</summary>
    Task UnlinkDeviceAsync(string deviceId, CancellationToken ct);

    /// <summary>Eigener Speicher: belegt in den eigenen Spaces und das Kontingent (null = unbegrenzt).</summary>
    Task<StorageInfo> GetStorageAsync(CancellationToken ct);

    Task<ClientAppsInfo> GetClientAppsAsync(CancellationToken ct);

    /// <summary>Freigabedialog: Eingeladene, Link und Dateianfrage eines Elements (nur wer den Space verwaltet).</summary>
    Task<SharingResponse> GetSharingAsync(string spaceId, string path, CancellationToken ct);

    /// <summary>Lädt einen Benutzer ein oder ändert seine Rolle.</summary>
    Task<InviteeInfo> InviteAsync(string spaceId, string path, string userName, string role, CancellationToken ct);

    /// <summary>Hebt eine Einladung auf; mit der eigenen Einladung verlässt man die Freigabe.</summary>
    Task RemoveInviteAsync(string shareId, CancellationToken ct);

    /// <summary>Legt den öffentlichen Link oder die Dateianfrage an oder ändert sie.</summary>
    Task<LinkInfo> SetLinkAsync(string spaceId, string path, string kind, LinkRequest request, CancellationToken ct);

    Task RemoveLinkAsync(string linkId, CancellationToken ct);

    /// <summary>„Shared with others“ und „File Request“.</summary>
    Task<IReadOnlyList<SharedItem>> SharedByMeAsync(CancellationToken ct);

    /// <summary>Admin-Konsole: alle Benutzer (nur Administratoren).</summary>
    Task<IReadOnlyList<AdminUserInfo>> ListUsersAsync(CancellationToken ct);

    Task<AdminUserInfo> CreateUserAsync(CreateUserRequest request, CancellationToken ct);

    Task<AdminUserInfo> UpdateUserAsync(string userId, UpdateUserRequest request, CancellationToken ct);

    /// <summary>Neues Passwort; alle Geräte des Benutzers werden abgemeldet.</summary>
    Task SetUserPasswordAsync(string userId, string password, CancellationToken ct);

    /// <summary>Freigabe-Richtlinien des Servers (für alle lesbar, damit die Oberfläche sich anpasst).</summary>
    Task<SharingPolicies> GetPoliciesAsync(CancellationToken ct);

    Task<SharingPolicies> UpdatePoliciesAsync(SharingPolicies policies, CancellationToken ct);

    Task<IReadOnlyList<GroupInfo>> ListGroupsAsync(CancellationToken ct);

    Task<GroupInfo> CreateGroupAsync(string name, CancellationToken ct);

    Task DeleteGroupAsync(string groupId, CancellationToken ct);

    Task<GroupInfo> SetGroupMemberAsync(string groupId, string userId, bool member, CancellationToken ct);

    Task<IReadOnlyList<TeamFolderInfo>> ListTeamFoldersAsync(CancellationToken ct);

    Task<TeamFolderInfo> CreateTeamFolderAsync(string name, CancellationToken ct);

    Task<TeamFolderInfo> RenameTeamFolderAsync(string spaceId, string name, CancellationToken ct);

    Task DeleteTeamFolderAsync(string spaceId, CancellationToken ct);

    /// <summary>Benutzer (<c>user</c>) oder Gruppe (<c>group</c>) mit Rolle in den Team-Ordner aufnehmen oder die Rolle ändern.</summary>
    Task<TeamFolderInfo> SetTeamMemberAsync(string spaceId, string kind, string id, string role, CancellationToken ct);

    Task<TeamFolderInfo> RemoveTeamMemberAsync(string spaceId, string kind, string id, CancellationToken ct);

    /// <summary>
    /// Wartet (Long-Poll) auf eine Änderung in allem, was der Benutzer sieht; liefert den neuen Stand, bei Zeitablauf den alten. Ohne
    /// <paramref name="after"/> sofort den aktuellen.
    /// </summary>
    Task<string> WaitForAccountChangesAsync(string? after, CancellationToken ct);
}

/// <summary>Öffentliche Links ohne Anmeldung (Seite <c>/s/{token}</c>).</summary>
public interface IPublicLinkApi
{
    /// <summary>Was der Link zeigt; ob noch ein Passwort fehlt.</summary>
    Task<PublicLinkInfo> GetLinkAsync(string token, CancellationToken ct);

    /// <summary>Prüft das Passwort; danach merkt sich der Browser die Freischaltung (Cookie nur für diesen Link).</summary>
    Task<PublicLinkInfo> UnlockLinkAsync(string token, string password, CancellationToken ct);

    Task<IReadOnlyList<DriveItem>> ListLinkAsync(string token, string path, CancellationToken ct);

    /// <summary>Datei eines Ordner-Links (oder ohne Pfad: die Datei eines Datei-Links).</summary>
    string LinkDownloadUrl(string token, string? path);

    /// <summary>Ohne Pfade der ganze Ordner.</summary>
    string LinkZipUrl(string token, IReadOnlyList<string> paths);

    /// <summary>Dateianfrage: zerlegt im Browser, lädt nur Fehlendes hoch; Ergebnis ist der Name, unter dem die Datei abgelegt wurde.</summary>
    Task<PublicUploadResponse> UploadToLinkAsync(string token, string name, Stream content, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>
/// <see cref="IDriveApi"/> über HTTP mit dem Sitzungs-Cookie des Browsers. Jede Anfrage trägt die Kopfzeile der eigenen Seite (ADR-11).
/// Hochladen zerlegt im Browser wie der Desktop-Client: nur fehlende Blöcke gehen über die Leitung.
/// </summary>
public sealed class HttpDriveApi : IDriveApi, IPublicLinkApi
{
    public const string CsrfHeader = "X-SyncDrive-Web";

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly HttpClient _http;
    private readonly ChunkerOptions _chunking;

    /// <param name="http">Mit der Adresse der eigenen Seite; der Browser hängt das Cookie selbst an.</param>
    public HttpDriveApi(HttpClient http, ChunkerOptions? chunking = null)
    {
        _http = http;
        _chunking = chunking ?? new ChunkerOptions();
        if (!_http.DefaultRequestHeaders.Contains(CsrfHeader))
        {
            _http.DefaultRequestHeaders.Add(CsrfHeader, "1");
        }
    }

    public async Task<PortalInfo> GetPortalAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/web/portal", ct);
        return await ReadAsync<PortalInfo>(response, ct);
    }

    public async Task<OAuthLoginInfo> GetOAuthLoginAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/web/oauth", ct);
        return await ReadAsync<OAuthLoginInfo>(response, ct);
    }

    public async Task<OAuthSettingsInfo> GetOAuthSettingsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/admin/oauth", ct);
        return await ReadAsync<OAuthSettingsInfo>(response, ct);
    }

    public async Task<OAuthSettingsInfo> SaveOAuthSettingsAsync(OAuthSettingsRequest request, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("api/v1/admin/oauth", request, Json, ct);
        return await ReadAsync<OAuthSettingsInfo>(response, ct);
    }

    public async Task<PortalInfo> UpdatePortalAsync(string serverName, string? loginMessage, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("api/v1/web/portal", new PortalUpdateRequest(serverName, loginMessage), Json, ct);
        return await ReadAsync<PortalInfo>(response, ct);
    }

    public async Task<PortalInfo> SetPortalBackgroundAsync(Stream image, CancellationToken ct)
    {
        using var body = new StreamContent(image);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PutAsync("api/v1/web/portal/background", body, ct);
        return await ReadAsync<PortalInfo>(response, ct);
    }

    public async Task<PortalInfo> RemovePortalBackgroundAsync(CancellationToken ct)
    {
        using var response = await _http.DeleteAsync("api/v1/web/portal/background", ct);
        return await ReadAsync<PortalInfo>(response, ct);
    }

    public async Task SetAvatarAsync(Stream image, CancellationToken ct)
    {
        using var body = new StreamContent(image);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PutAsync("api/v1/account/avatar", body, ct);
        await EnsureAsync(response, ct);
    }

    public async Task RemoveAvatarAsync(CancellationToken ct)
    {
        using var response = await _http.DeleteAsync("api/v1/account/avatar", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<WebSessionInfo?> GetSessionAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/web/session", ct);
        return response.StatusCode == HttpStatusCode.Unauthorized ? null : await ReadAsync<WebSessionInfo>(response, ct);
    }

    public async Task<WebSessionInfo> SignInAsync(string userName, string password, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/web/session", new WebSignInRequest(userName, password), Json, ct);
        return await ReadAsync<WebSessionInfo>(response, ct);
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        using var response = await _http.DeleteAsync("api/v1/web/session", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<SpaceSummary>> ListSpacesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/spaces", ct);
        return (await ReadAsync<SpacesResponse>(response, ct)).Spaces;
    }

    public async Task<IReadOnlyList<DriveItem>> ListAsync(string spaceId, string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/spaces/{Escape(spaceId)}/snapshot?path={Escape(path)}&depth=1", ct);
        await EnsureAsync(response, ct);
        var items = new List<DriveItem>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 0 && JsonSerializer.Deserialize<SnapshotEntry>(line, Json) is { } entry)
            {
                items.Add(ToItem(entry));
            }
        }

        return items;
    }

    public Task CreateFolderAsync(string spaceId, string path, CancellationToken ct) =>
        CommitAsync(spaceId, new CommitRequest(CommitOps.Mkdir, path, 0, MTime: DateTimeOffset.UtcNow), ct);

    public Task MoveAsync(string spaceId, DriveItem item, string newPath, CancellationToken ct) =>
        CommitAsync(spaceId, new CommitRequest(CommitOps.Move, item.Path, item.Revision, NewPath: newPath), ct);

    public Task CopyAsync(string spaceId, DriveItem item, string newPath, CancellationToken ct) =>
        CommitAsync(spaceId, new CommitRequest(CommitOps.Copy, item.Path, 0, NewPath: newPath), ct);

    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/info", ct);
        return await ReadAsync<ServerInfo>(response, ct);
    }

    public Task DeleteAsync(string spaceId, DriveItem item, CancellationToken ct) =>
        CommitAsync(spaceId, new CommitRequest(CommitOps.Delete, item.Path, item.Revision), ct);

    public async Task UploadAsync(
        string spaceId, string path, Stream content, DateTimeOffset modified, DriveItem? replaces, IProgress<long>? progress, CancellationToken ct)
    {
        var chunked = await SendBlocksAsync(content, $"api/v1/spaces/{Escape(spaceId)}/blocks/missing", "api/v1/blocks", progress, ct);
        await CommitAsync(spaceId, new CommitRequest(
            CommitOps.Put, path, replaces?.Revision ?? 0, Blocks: [.. chunked.Chunks.Select(c => c.Hash.Value)], Size: chunked.Size,
            MTime: modified, FileHash: chunked.FileHash.Value), ct);
    }

    public string DownloadUrl(string spaceId, string path, long? revision = null) =>
        $"api/v1/spaces/{Escape(spaceId)}/download?path={Escape(path)}" + (revision is { } r ? $"&revision={r}" : string.Empty);

    public string ThumbnailUrl(string spaceId, string path, long revision, bool large = false) =>
        $"api/v1/spaces/{Escape(spaceId)}/thumbnail?path={Escape(path)}&v={revision}" + (large ? "&size=large" : string.Empty);

    public string ViewUrl(string spaceId, string path, long revision) =>
        $"api/v1/spaces/{Escape(spaceId)}/view?path={Escape(path)}&v={revision}";

    public async Task<byte[]> ReadFileAsync(string spaceId, string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(DownloadUrl(spaceId, path), ct);
        await EnsureAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public Task<IReadOnlyList<DriveItem>> SearchAsync(string spaceId, string path, string query, CancellationToken ct) =>
        FindAsync($"api/v1/spaces/{Escape(spaceId)}/search?q={Escape(query)}&path={Escape(path)}", ct);

    public Task<IReadOnlyList<DriveItem>> RecentAsync(string spaceId, CancellationToken ct) =>
        FindAsync($"api/v1/spaces/{Escape(spaceId)}/recent?limit=50", ct);

    public async Task<IReadOnlyList<TrashEntry>> ListTrashAsync(string spaceId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/spaces/{Escape(spaceId)}/trash", ct);
        return (await ReadAsync<TrashResponse>(response, ct)).Entries;
    }

    public async Task RestoreFromTrashAsync(string spaceId, string nodeId, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"api/v1/spaces/{Escape(spaceId)}/trash/restore", new RestoreTrashRequest(nodeId), Json, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(string spaceId, string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/spaces/{Escape(spaceId)}/versions?path={Escape(path)}", ct);
        return [.. (await ReadAsync<VersionsResponse>(response, ct)).Versions.OrderByDescending(v => v.Revision)];
    }

    public async Task<long> RestoreVersionAsync(string spaceId, string path, long revision, long currentRevision, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/spaces/{Escape(spaceId)}/versions/restore", new RestoreVersionRequest(path, revision, currentRevision), Json, ct);
        return (await ReadAsync<CommitResponse>(response, ct)).Revision;
    }

    private async Task<IReadOnlyList<DriveItem>> FindAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        return [.. (await ReadAsync<FindResponse>(response, ct)).Entries.Select(ToItem)];
    }

    private static DriveItem ToItem(SnapshotEntry entry) =>
        new(entry.Path, entry.Kind == NodeKinds.Directory, entry.Size, entry.MTime, entry.Revision, entry.NodeId);

    public string ZipUrl(string spaceId, IReadOnlyList<string> paths) =>
        $"api/v1/spaces/{Escape(spaceId)}/zip?" + string.Join('&', paths.Select(p => "path=" + Escape(p)));

    public async Task<IReadOnlyList<ItemRef>> ListStarsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/stars", ct);
        return (await ReadAsync<ItemsResponse>(response, ct)).Items;
    }

    public async Task SetStarAsync(string spaceId, string path, bool starred, CancellationToken ct)
    {
        var url = $"api/v1/spaces/{Escape(spaceId)}/star?path={Escape(path)}";
        using var response = starred ? await _http.PutAsync(url, null, ct) : await _http.DeleteAsync(url, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/labels", ct);
        return (await ReadAsync<LabelsResponse>(response, ct)).Labels;
    }

    public async Task<LabelInfo> CreateLabelAsync(string name, string color, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/labels", new CreateLabelRequest(name, color), Json, ct);
        return await ReadAsync<LabelInfo>(response, ct);
    }

    public async Task DeleteLabelAsync(string labelId, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v1/labels/{Escape(labelId)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<ItemRef>> ListLabeledAsync(string labelId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/labels/{Escape(labelId)}/items", ct);
        return (await ReadAsync<ItemsResponse>(response, ct)).Items;
    }

    public async Task SetLabelAsync(string labelId, string spaceId, string path, bool assigned, CancellationToken ct)
    {
        var url = $"api/v1/spaces/{Escape(spaceId)}/labels/{Escape(labelId)}?path={Escape(path)}";
        using var response = assigned ? await _http.PutAsync(url, null, ct) : await _http.DeleteAsync(url, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<NotificationInfo>> ListNotificationsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/notifications", ct);
        return (await ReadAsync<NotificationsResponse>(response, ct)).Notifications;
    }

    public async Task MarkNotificationReadAsync(string notificationId, CancellationToken ct)
    {
        using var response = await _http.PostAsync($"api/v1/notifications/{Escape(notificationId)}/read", null, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/auth/devices", ct);
        return (await ReadAsync<DevicesResponse>(response, ct)).Devices;
    }

    public async Task UnlinkDeviceAsync(string deviceId, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v1/auth/devices/{Escape(deviceId)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<ClientAppsInfo> GetClientAppsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/web/apps", ct);
        return await ReadAsync<ClientAppsInfo>(response, ct);
    }

    public async Task<StorageInfo> GetStorageAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/account/storage", ct);
        return await ReadAsync<StorageInfo>(response, ct);
    }

    public async Task<SharingResponse> GetSharingAsync(string spaceId, string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/spaces/{Escape(spaceId)}/sharing?path={Escape(path)}", ct);
        return await ReadAsync<SharingResponse>(response, ct);
    }

    public async Task<InviteeInfo> InviteAsync(string spaceId, string path, string userName, string role, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/spaces/{Escape(spaceId)}/sharing/invitees?path={Escape(path)}", new InviteRequest(userName, role), Json, ct);
        return await ReadAsync<InviteeInfo>(response, ct);
    }

    public async Task RemoveInviteAsync(string shareId, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v1/sharing/invitees/{Escape(shareId)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<LinkInfo> SetLinkAsync(string spaceId, string path, string kind, LinkRequest request, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync(
            $"api/v1/spaces/{Escape(spaceId)}/sharing/link?path={Escape(path)}&kind={Escape(kind)}", request, Json, ct);
        return await ReadAsync<LinkInfo>(response, ct);
    }

    public async Task RemoveLinkAsync(string linkId, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v1/sharing/links/{Escape(linkId)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<SharedItem>> SharedByMeAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/sharing/by-me", ct);
        return (await ReadAsync<SharedItemsResponse>(response, ct)).Items;
    }

    public async Task<IReadOnlyList<AdminUserInfo>> ListUsersAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/admin/users", ct);
        return (await ReadAsync<AdminUsersResponse>(response, ct)).Users;
    }

    public async Task<AdminUserInfo> CreateUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/admin/users", request, Json, ct);
        return await ReadAsync<AdminUserInfo>(response, ct);
    }

    public async Task<AdminUserInfo> UpdateUserAsync(string userId, UpdateUserRequest request, CancellationToken ct)
    {
        using var response = await _http.PatchAsJsonAsync($"api/v1/admin/users/{Escape(userId)}", request, Json, ct);
        return await ReadAsync<AdminUserInfo>(response, ct);
    }

    public async Task SetUserPasswordAsync(string userId, string password, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync($"api/v1/admin/users/{Escape(userId)}/password", new SetPasswordRequest(password), Json, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<SharingPolicies> GetPoliciesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/policies", ct);
        return await ReadAsync<SharingPolicies>(response, ct);
    }

    public async Task<SharingPolicies> UpdatePoliciesAsync(SharingPolicies policies, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("api/v1/admin/policies", policies, Json, ct);
        return await ReadAsync<SharingPolicies>(response, ct);
    }

    public async Task<IReadOnlyList<GroupInfo>> ListGroupsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/admin/groups", ct);
        return (await ReadAsync<GroupsResponse>(response, ct)).Groups;
    }

    public async Task<GroupInfo> CreateGroupAsync(string name, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/admin/groups", new NameRequest(name), Json, ct);
        return await ReadAsync<GroupInfo>(response, ct);
    }

    public async Task DeleteTeamFolderAsync(string spaceId, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v1/admin/team-folders/{Escape(spaceId)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task DeleteGroupAsync(string groupId, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v1/admin/groups/{Escape(groupId)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<GroupInfo> SetGroupMemberAsync(string groupId, string userId, bool member, CancellationToken ct)
    {
        var url = $"api/v1/admin/groups/{Escape(groupId)}/members/{Escape(userId)}";
        using var response = member ? await _http.PutAsync(url, null, ct) : await _http.DeleteAsync(url, ct);
        return await ReadAsync<GroupInfo>(response, ct);
    }

    public async Task<IReadOnlyList<TeamFolderInfo>> ListTeamFoldersAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/admin/team-folders", ct);
        return (await ReadAsync<TeamFoldersResponse>(response, ct)).TeamFolders;
    }

    public async Task<TeamFolderInfo> CreateTeamFolderAsync(string name, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/admin/team-folders", new NameRequest(name), Json, ct);
        return await ReadAsync<TeamFolderInfo>(response, ct);
    }

    public async Task<TeamFolderInfo> RenameTeamFolderAsync(string spaceId, string name, CancellationToken ct)
    {
        using var response = await _http.PatchAsJsonAsync($"api/v1/admin/team-folders/{Escape(spaceId)}", new NameRequest(name), Json, ct);
        return await ReadAsync<TeamFolderInfo>(response, ct);
    }

    public async Task<TeamFolderInfo> SetTeamMemberAsync(string spaceId, string kind, string id, string role, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync(
            $"api/v1/admin/team-folders/{Escape(spaceId)}/members", new TeamMemberRequest(kind, id, role), Json, ct);
        return await ReadAsync<TeamFolderInfo>(response, ct);
    }

    public async Task<TeamFolderInfo> RemoveTeamMemberAsync(string spaceId, string kind, string id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v1/admin/team-folders/{Escape(spaceId)}/members/{Escape(kind)}/{Escape(id)}", ct);
        return await ReadAsync<TeamFolderInfo>(response, ct);
    }

    public async Task<PublicLinkInfo> GetLinkAsync(string token, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/public/{Escape(token)}", ct);
        return await ReadAsync<PublicLinkInfo>(response, ct);
    }

    public async Task<PublicLinkInfo> UnlockLinkAsync(string token, string password, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"api/v1/public/{Escape(token)}/password", new LinkPasswordRequest(password), Json, ct);
        return await ReadAsync<PublicLinkInfo>(response, ct);
    }

    public async Task<IReadOnlyList<DriveItem>> ListLinkAsync(string token, string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/public/{Escape(token)}/list?path={Escape(path)}", ct);
        return [.. (await ReadAsync<FindResponse>(response, ct)).Entries.Select(ToItem)];
    }

    public string LinkDownloadUrl(string token, string? path) =>
        $"api/v1/public/{Escape(token)}/download" + (path is null ? string.Empty : $"?path={Escape(path)}");

    public string LinkZipUrl(string token, IReadOnlyList<string> paths) =>
        $"api/v1/public/{Escape(token)}/zip" + (paths.Count == 0 ? string.Empty : "?" + string.Join('&', paths.Select(p => "path=" + Escape(p))));

    public async Task<PublicUploadResponse> UploadToLinkAsync(string token, string name, Stream content, IProgress<long>? progress, CancellationToken ct)
    {
        var at = $"api/v1/public/{Escape(token)}";
        var chunked = await SendBlocksAsync(content, at + "/blocks/missing", at + "/blocks", progress, ct);
        using var response = await _http.PostAsJsonAsync(
            at + "/files", new PublicFileRequest(name, [.. chunked.Chunks.Select(c => c.Hash.Value)], chunked.Size, chunked.FileHash.Value), Json, ct);
        return await ReadAsync<PublicUploadResponse>(response, ct);
    }

    public async Task<string> WaitForAccountChangesAsync(string? after, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            after is null ? "api/v1/account/changes/wait" : $"api/v1/account/changes/wait?after={Uri.EscapeDataString(after)}", ct);
        return (await ReadAsync<AccountChangesResponse>(response, ct)).Stamp;
    }

    /// <summary>Zerlegt im Browser und schickt nur Blöcke, die der Server für dieses Ziel noch nicht hat (<paramref name="missing"/>, dann <c>{blocks}/{hash}</c>).</summary>
    private async Task<ChunkedFile> SendBlocksAsync(Stream content, string missing, string blocks, IProgress<long>? progress, CancellationToken ct)
    {
        long sent = 0;
        return await FastCdcChunker.ChunkAsync(content, _chunking, async (chunk, data, token) =>
        {
            using (var check = await _http.PostAsJsonAsync(missing, new MissingBlocksRequest([chunk.Hash.Value]), Json, token))
            {
                if ((await ReadAsync<MissingBlocksResponse>(check, token)).Missing.Count > 0)
                {
                    using var body = new ReadOnlyMemoryContent(data);
                    using var put = await _http.PutAsync($"{blocks}/{Escape(chunk.Hash.Value)}", body, token);
                    await EnsureAsync(put, token);
                }
            }

            sent += chunk.Length;
            progress?.Report(sent);
        }, ct);
    }

    private async Task CommitAsync(string spaceId, CommitRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"api/v1/spaces/{Escape(spaceId)}/commit", request, Json, ct);
        await EnsureAsync(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? await response.Content.ReadFromJsonAsync<ProblemDetail>(Json, ct)
            : null;
        throw new DriveException(problem?.Code ?? $"http_{(int)response.StatusCode}", problem?.Title ?? response.ReasonPhrase ?? "Fehler");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
