using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Contracts;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Control;

/// <summary>Zeile der Aufgabenliste (Hauptfenster „Sync Tasks“): gruppiert nach Server, mit Modus, Cloud-Hinweis und lokalem Pfad.</summary>
public sealed record TaskSummary(
    string TaskId,
    string ConnectionId,
    string ServerName,
    string ServerAddress,
    string SpaceId,
    string RemotePath,
    string LocalPath,
    SyncDirection Direction,
    bool OnDemand,
    bool Paused);

public sealed record ConnectionSummary(string Id, string DisplayName, string ServerAddress, string UserName);

/// <param name="TrustedCertificateSha256">Fingerabdruck, den der Nutzer nach Rückfrage bestätigt hat (selbstsigniertes Zertifikat).</param>
public sealed record SignInRequest(string ServerAddress, string UserName, string Password, string? TrustedCertificateSha256 = null);

/// <summary>Ergebnis der Anmeldung. Bei <see cref="SetupErrors.UntrustedCertificate"/> steht der Fingerabdruck zur Rückfrage in <see cref="CertificateSha256"/>.</summary>
public sealed record SignInOutcome(string? ConnectionId, string? ErrorCode = null, string? Message = null, string? CertificateSha256 = null)
{
    public bool Succeeded => ConnectionId is not null;
}

/// <summary>Ordner im Serverbaum der Ordnerauswahl. Auf oberster Ebene sind es die Spaces („My Drive“, Team-Ordner).</summary>
public sealed record ServerFolder(string SpaceId, string Path, string Name, bool IsSpace, bool CanWrite);

public sealed record NewTaskRequest(
    string ConnectionId,
    string SpaceId,
    string RemotePath,
    string LocalPath,
    SyncDirection Direction = SyncDirection.TwoWay,
    bool KeepLocallyDeletedOnServer = true,
    bool OnDemand = false,
    bool AdvancedConsistencyCheck = false,
    TaskFilterSettings? Filters = null);

/// <summary>Angelegt (<see cref="TaskId"/>) oder abgelehnt (Regelverstöße bzw. Fehler).</summary>
public sealed record TaskOutcome(string? TaskId, IReadOnlyList<TaskRuleViolation> Violations, string? ErrorCode = null, string? Message = null)
{
    public bool Succeeded => TaskId is not null;
}

public static class SetupErrors
{
    public const string UntrustedCertificate = "untrusted_certificate";
    public const string Unreachable = "unreachable";
    public const string InvalidAddress = "invalid_address";
    public const string SignInFailed = "sign_in_failed";

    /// <summary>Der Server bremst nach zu vielen Fehlversuchen (429); später erneut versuchen.</summary>
    public const string TooManyAttempts = "too_many_attempts";
    public const string UnknownConnection = "unknown_connection";
    public const string UnknownTask = "unknown_task";
}

/// <summary>Einrichtung und Verwaltung: Assistent, Aufgabenliste, Einstellungen. Der Daemon ist der einzige, der Konfiguration und Token schreibt.</summary>
public interface IClientSetup
{
    Task<IReadOnlyList<TaskSummary>> GetTasksAsync(CancellationToken ct);

    Task<IReadOnlyList<ConnectionSummary>> GetConnectionsAsync(CancellationToken ct);

    Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct);

    /// <summary>Ohne <paramref name="spaceId"/> die Spaces, sonst die Unterordner von <paramref name="path"/> (eine Ebene).</summary>
    Task<IReadOnlyList<ServerFolder>> BrowseAsync(string connectionId, string? spaceId, string path, CancellationToken ct);

    /// <summary>Prüft einen lokalen Ordner sofort bei der Auswahl (verschachtelt, anderer Cloud-Dienst, On-Demand-Voraussetzungen).</summary>
    Task<IReadOnlyList<TaskRuleViolation>> CheckLocalFolderAsync(string localPath, bool onDemand, CancellationToken ct);

    Task<TaskOutcome> CreateTaskAsync(NewTaskRequest request, CancellationToken ct);

    /// <summary>Entfernt die Aufgabe; lokale Dateien bleiben.</summary>
    Task DeleteTaskAsync(string taskId, CancellationToken ct);

    Task<ClientSettings> GetSettingsAsync(CancellationToken ct);

    Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct);

    /// <summary>Vollständige Aufgabe für „Sync Rules“.</summary>
    Task<SyncTaskDefinition?> GetTaskAsync(string taskId, CancellationToken ct);

    /// <summary>„Sync Rules“ speichern (Ordner, Dateifilter, Sync-Modus); die Aufgabe startet mit den neuen Regeln neu.</summary>
    Task<TaskOutcome> UpdateTaskAsync(SyncTaskDefinition task, CancellationToken ct);

    /// <summary>„Edit Connection“: neu anmelden, auch mit geänderter Adresse; die Aufgaben der Verbindung starten neu.</summary>
    Task<SignInOutcome> UpdateConnectionAsync(string connectionId, SignInRequest request, CancellationToken ct);

    /// <summary>„Usage“: belegter Speicher je Aufgabe (null, wenn der Server nicht antwortet).</summary>
    Task<IReadOnlyList<TaskUsage>> GetUsageAsync(CancellationToken ct);

    /// <summary>Proxy-Passwort in den Geheimnisspeicher (null löscht es).</summary>
    Task SetProxyPasswordAsync(string? password, CancellationToken ct);

    /// <summary>Benachrichtigungen aller Server, neueste zuerst.</summary>
    Task<IReadOnlyList<ServerNotification>> GetNotificationsAsync(CancellationToken ct);

    /// <summary>Alle als gelesen markieren (Panel-Reiter geöffnet); der rote Punkt verschwindet.</summary>
    Task MarkNotificationsReadAsync(CancellationToken ct);
}

public sealed record TaskUsage(string TaskId, long? Bytes, long? Files);

/// <summary>Benachrichtigung mit Server: „anna shared Familie“ (Typ <c>shared</c>).</summary>
public sealed record ServerNotification(
    string Id, string ConnectionId, string ServerName, string Type, DateTimeOffset CreatedAt, string? Actor, string? SpaceName, string? Path, bool Read);

/// <summary>Explorer-Menü der Sync-Ordner; nach Änderungen an Aufgaben, Sprache oder „Kontextmenü anzeigen“ neu schreiben.</summary>
public interface IShellIntegration
{
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>Anmeldung am Rechner (Autostart). Windows: Eintrag unter <c>HKCU\…\Run</c>.</summary>
public interface IAutostart
{
    void Apply(bool enabled);
}

/// <summary>Umsetzung im Daemon: Konfiguration, Geheimnisse, Server-Zugriff und Aufgabenlauf hinter einer Schnittstelle.</summary>
public sealed class ClientSetup(
    IClientConfigStore config,
    ICredentialStore credentials,
    IServerAccess server,
    ITaskLifecycle lifecycle,
    Func<bool, TaskEnvironment> environment,
    string deviceName,
    IAutostart? autostart = null,
    NotificationCenter? notifications = null,
    IShellIntegration? shellIntegration = null,
    ICloudFolders? cloudFolders = null) : IClientSetup
{
    /// <summary>Port, wenn die Adresse keinen nennt (Standard des Server-Containers, <c>deploy/</c>).</summary>
    public const int DefaultPort = 8443;

    public async Task<IReadOnlyList<TaskSummary>> GetTasksAsync(CancellationToken ct)
    {
        var connections = (await config.ListConnectionsAsync(ct)).ToDictionary(c => c.Id);
        return [.. (await config.ListTasksAsync(ct)).Select(t =>
        {
            var connection = connections.GetValueOrDefault(t.ConnectionId);
            return new TaskSummary(
                t.Id, t.ConnectionId, connection?.DisplayName ?? "?", connection?.ServerUrl.Authority ?? "?", t.SpaceId, t.RemotePath.Value,
                t.LocalPath, t.Direction, t.OnDemand, t.Paused);
        })];
    }

    public async Task<IReadOnlyList<ConnectionSummary>> GetConnectionsAsync(CancellationToken ct) =>
        [.. (await config.ListConnectionsAsync(ct)).Select(c => new ConnectionSummary(c.Id, c.DisplayName, c.ServerUrl.Authority, c.UserName))];

    public Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct) => SignInCoreAsync(request, null, ct);

    public async Task<SignInOutcome> UpdateConnectionAsync(string connectionId, SignInRequest request, CancellationToken ct)
    {
        if ((await config.ListConnectionsAsync(ct)).All(c => c.Id != connectionId))
        {
            return new SignInOutcome(null, SetupErrors.UnknownConnection, "Verbindung unbekannt.");
        }

        var outcome = await SignInCoreAsync(request, connectionId, ct);
        if (outcome.Succeeded)
        {
            foreach (var task in (await config.ListTasksAsync(ct)).Where(t => t.ConnectionId == connectionId))
            {
                await RestartAsync(task);
            }
        }

        return outcome;
    }

    private async Task<SignInOutcome> SignInCoreAsync(SignInRequest request, string? connectionId, CancellationToken ct)
    {
        if (ParseAddress(request.ServerAddress) is not { } url)
        {
            return new SignInOutcome(null, SetupErrors.InvalidAddress, "Adresse nicht verständlich.");
        }

        var probe = await server.ProbeAsync(url, ct);
        if (!probe.IsSuccess)
        {
            return new SignInOutcome(null, SetupErrors.Unreachable, probe.Error!.Message);
        }

        string? pin = null;
        if (!probe.Value!.Trusted)
        {
            if (!string.Equals(request.TrustedCertificateSha256, probe.Value.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new SignInOutcome(null, SetupErrors.UntrustedCertificate, "Zertifikat nicht vertrauenswürdig.", probe.Value.CertificateSha256);
            }

            pin = probe.Value.CertificateSha256;
        }

        var registered = await server.RegisterDeviceAsync(url, request.UserName, request.Password, deviceName, pin, ct);
        if (!registered.IsSuccess)
        {
            var code = registered.Error!.Code switch
            {
                ErrorCode.Unauthorized => SetupErrors.SignInFailed,
                ErrorCode.TooManyAttempts => SetupErrors.TooManyAttempts,
                _ => SetupErrors.Unreachable,
            };
            return new SignInOutcome(null, code, registered.Error.Message);
        }

        var existing = (await config.ListConnectionsAsync(ct)).FirstOrDefault(c => connectionId is not null
            ? c.Id == connectionId
            : c.ServerUrl == url && string.Equals(c.UserName, request.UserName, StringComparison.OrdinalIgnoreCase));
        var connection = new ConnectionInfo(
            existing?.Id ?? "c_" + Guid.NewGuid().ToString("N")[..12], url.Host, url, request.UserName, registered.Value!.DeviceId, pin);
        await credentials.SaveAsync(CredentialKeys.DeviceToken(connection.Id), registered.Value.Token, ct);
        await config.SaveConnectionAsync(connection, ct);
        return new SignInOutcome(connection.Id);
    }

    public async Task<IReadOnlyList<ServerFolder>> BrowseAsync(string connectionId, string? spaceId, string path, CancellationToken ct)
    {
        var login = await LoginAsync(connectionId, ct);
        var spaces = await server.ListSpacesAsync(login, ct);
        if (!spaces.IsSuccess)
        {
            return [];
        }

        if (spaceId is null)
        {
            return [.. spaces.Value!.Select(s => new ServerFolder(s.Id, string.Empty, s.Name, IsSpace: true, CanWrite(s.Role)))];
        }

        var canWrite = spaces.Value!.FirstOrDefault(s => s.Id == spaceId) is { } space && CanWrite(space.Role);
        var children = await server.ListChildrenAsync(login, spaceId, RelativePath.Parse(path), ct);
        return children.IsSuccess
            ? [.. children.Value!.Where(e => e.Kind == NodeKinds.Directory).Select(e =>
                new ServerFolder(spaceId, e.Path, RelativePath.Parse(e.Path).Name, IsSpace: false, canWrite))]
            : [];
    }

    public async Task<IReadOnlyList<TaskRuleViolation>> CheckLocalFolderAsync(string localPath, bool onDemand, CancellationToken ct)
    {
        var candidate = new SyncTaskDefinition("check", "-", "-", RelativePath.Root, localPath, OnDemand: onDemand);
        var violations = SyncTaskRules.Validate(candidate, await config.ListTasksAsync(ct), environment(true));
        return [.. violations.Where(v => v != TaskRuleViolation.TooManyTasks)];
    }

    public async Task<TaskOutcome> CreateTaskAsync(NewTaskRequest request, CancellationToken ct)
    {
        var login = await LoginAsync(request.ConnectionId, ct);
        var spaces = await server.ListSpacesAsync(login, ct);
        var canWrite = !spaces.IsSuccess || (spaces.Value!.FirstOrDefault(s => s.Id == request.SpaceId) is { } space && CanWrite(space.Role));
        var task = new SyncTaskDefinition(
            "t_" + Guid.NewGuid().ToString("N")[..12], request.ConnectionId, request.SpaceId, RelativePath.Parse(request.RemotePath), request.LocalPath,
            request.Direction, request.KeepLocallyDeletedOnServer, request.OnDemand, request.AdvancedConsistencyCheck, request.Filters);

        var violations = SyncTaskRules.Validate(task, await config.ListTasksAsync(ct), environment(canWrite));
        if (violations.Count > 0)
        {
            return new TaskOutcome(null, violations);
        }

        Directory.CreateDirectory(task.LocalPath);
        await config.SaveTaskAsync(task, ct);
        lifecycle.Start(task);
        await RefreshShellAsync(ct);
        return new TaskOutcome(task.Id, []);
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        var task = await GetTaskAsync(taskId, ct);
        await lifecycle.StopAsync(taskId);
        await config.RemoveTaskAsync(taskId, ct);
        if (task is not null && cloudFolders is not null)
        {
            await cloudFolders.DetachAsync(task, ct); // erst jetzt: der angehaltene Sync sieht das Entfernen der Platzhalter nicht mehr
        }

        await RefreshShellAsync(ct);
    }

    public Task<ClientSettings> GetSettingsAsync(CancellationToken ct) => config.GetSettingsAsync(ct);

    public async Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct)
    {
        var before = await config.GetSettingsAsync(ct);
        await config.SaveSettingsAsync(settings, ct);
        autostart?.Apply(settings.StartWithWindows);
        if (before.ShowContextMenu != settings.ShowContextMenu || before.Language != settings.Language)
        {
            await RefreshShellAsync(ct);
        }

        if (before.Proxy != settings.Proxy || before.Conflict != settings.Conflict || before.RenameDiscarded != settings.RenameDiscarded
            || before.OnRestart != settings.OnRestart)
        {
            await RestartAllAsync(ct); // Proxy und Konfliktregeln gelten ab dem nächsten Lauf jeder Aufgabe
        }
    }

    public async Task<SyncTaskDefinition?> GetTaskAsync(string taskId, CancellationToken ct) =>
        (await config.ListTasksAsync(ct)).FirstOrDefault(t => t.Id == taskId);

    public async Task<TaskOutcome> UpdateTaskAsync(SyncTaskDefinition task, CancellationToken ct)
    {
        var tasks = await config.ListTasksAsync(ct);
        if (tasks.FirstOrDefault(t => t.Id == task.Id) is not { } stored)
        {
            return new TaskOutcome(null, [], SetupErrors.UnknownTask, "Aufgabe unbekannt.");
        }

        // „Sync Rules“ ändern Regeln und Modus, nicht Ort oder Verbindung.
        var updated = stored with
        {
            Direction = task.Direction,
            KeepLocallyDeletedOnServer = task.KeepLocallyDeletedOnServer,
            AdvancedConsistencyCheck = task.AdvancedConsistencyCheck,
            Filters = task.Filters,
        };
        var violations = SyncTaskRules.Validate(updated, tasks, environment(await CanWriteAsync(stored.ConnectionId, stored.SpaceId, ct)));
        if (violations.Count > 0)
        {
            return new TaskOutcome(null, violations);
        }

        await config.SaveTaskAsync(updated, ct);
        await RestartAsync(updated);
        return new TaskOutcome(updated.Id, []);
    }

    public async Task<IReadOnlyList<TaskUsage>> GetUsageAsync(CancellationToken ct)
    {
        var result = new List<TaskUsage>();
        foreach (var task in await config.ListTasksAsync(ct))
        {
            var usage = await server.GetUsageAsync(await LoginAsync(task.ConnectionId, ct), task.SpaceId, task.RemotePath, ct);
            result.Add(usage.IsSuccess ? new TaskUsage(task.Id, usage.Value!.Bytes, usage.Value.Files) : new TaskUsage(task.Id, null, null));
        }

        return result;
    }

    public async Task SetProxyPasswordAsync(string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
        {
            await credentials.DeleteAsync(CredentialKeys.ProxyPassword, ct);
        }
        else
        {
            await credentials.SaveAsync(CredentialKeys.ProxyPassword, password, ct);
        }

        await RestartAllAsync(ct);
    }

    public Task<IReadOnlyList<ServerNotification>> GetNotificationsAsync(CancellationToken ct) =>
        notifications?.RefreshAsync(ct) ?? Task.FromResult<IReadOnlyList<ServerNotification>>([]);

    public Task MarkNotificationsReadAsync(CancellationToken ct) => notifications?.MarkAllReadAsync(ct) ?? Task.CompletedTask;

    /// <summary>Aktuelle Proxy-Einstellung samt Passwort für neue Verbindungen.</summary>
    public static async Task<ProxyConfig> ProxyAsync(IClientConfigStore config, ICredentialStore credentials, CancellationToken ct)
    {
        var proxy = (await config.GetSettingsAsync(ct)).Proxy;
        return proxy.Mode == ProxyMode.Manual
            ? new ProxyConfig(
                proxy.Mode, proxy.Host, proxy.Port, proxy.UseAuthentication ? proxy.UserName : null,
                proxy.UseAuthentication ? await credentials.GetAsync(CredentialKeys.ProxyPassword, ct) : null)
            : new ProxyConfig(proxy.Mode);
    }

    private Task RefreshShellAsync(CancellationToken ct) => shellIntegration?.RefreshAsync(ct) ?? Task.CompletedTask;

    private async Task RestartAsync(SyncTaskDefinition task)
    {
        await lifecycle.StopAsync(task.Id);
        lifecycle.Start(task);
    }

    private async Task RestartAllAsync(CancellationToken ct)
    {
        foreach (var task in await config.ListTasksAsync(ct))
        {
            await RestartAsync(task);
        }
    }

    private async Task<bool> CanWriteAsync(string connectionId, string spaceId, CancellationToken ct)
    {
        var spaces = await server.ListSpacesAsync(await LoginAsync(connectionId, ct), ct);
        return !spaces.IsSuccess || (spaces.Value!.FirstOrDefault(s => s.Id == spaceId) is { } space && CanWrite(space.Role));
    }

    /// <summary>„nas.local“, „nas.local:9000“, „https://nas.local:8443/“ → HTTPS-Adresse mit Port; ohne Schema immer HTTPS.</summary>
    public static Uri? ParseAddress(string address)
    {
        var text = address.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0)
        {
            return null;
        }

        var hasPort = !uri.IsDefaultPort || address.Contains($":{uri.Port}", StringComparison.Ordinal);
        return new UriBuilder(uri.Scheme, uri.Host, hasPort ? uri.Port : DefaultPort, "/").Uri;
    }

    private static bool CanWrite(string role) => role is "editor" or "manager" or "owner";

    private async Task<ServerLogin> LoginAsync(string connectionId, CancellationToken ct)
    {
        var connection = (await config.ListConnectionsAsync(ct)).FirstOrDefault(c => c.Id == connectionId)
            ?? throw new InvalidOperationException(SetupErrors.UnknownConnection);
        var token = await credentials.GetAsync(CredentialKeys.DeviceToken(connectionId), ct) ?? string.Empty;
        return new ServerLogin(connection.ServerUrl, token, connection.PinnedCertificateSha256);
    }
}

/// <summary>Sicht der Oberfläche auf den Daemon: Status, Einrichtung und Verbindungszustand (die Pipe kann abreißen).</summary>
public interface IDaemonClient : IDaemonControl, IClientSetup, IShellActions, IAsyncDisposable
{
    event Action? Disconnected;

    bool IsConnected { get; }
}
