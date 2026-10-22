using SyncDrive.Client.App.Ports;

namespace SyncDrive.Client.App.Control;

/// <summary>
/// Holt die Benachrichtigungen aller Verbindungen (Tray-Reiter „Notifications“, Seite „Notifications“) und hält den roten Punkt am
/// Tray-Symbol aktuell: gesetzt, solange etwas ungelesen ist. Abfrage regelmäßig und auf Anfrage der Oberfläche.
/// </summary>
public sealed class NotificationCenter(
    IClientConfigStore config,
    ICredentialStore credentials,
    IServerAccess server,
    DaemonController controller,
    TimeSpan? interval = null,
    TimeProvider? clock = null) : IDisposable
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ServerNotification> _current = [];

    public IReadOnlyList<ServerNotification> Current => _current;

    public void Dispose() => _gate.Dispose();

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct);
            try
            {
                await Task.Delay(_interval, _clock, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<IReadOnlyList<ServerNotification>> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = new List<ServerNotification>();
            foreach (var connection in await config.ListConnectionsAsync(ct))
            {
                if (await credentials.GetAsync(CredentialKeys.DeviceToken(connection.Id), ct) is not { } token)
                {
                    continue;
                }

                var login = new ServerLogin(connection.ServerUrl, token, connection.PinnedCertificateSha256);
                var notes = await server.ListNotificationsAsync(login, ct);
                if (!notes.IsSuccess)
                {
                    continue; // Server gerade nicht erreichbar: beim nächsten Mal
                }

                var spaces = await server.ListSpacesAsync(login, ct);
                var names = spaces.IsSuccess ? spaces.Value!.ToDictionary(s => s.Id, s => s.Name) : [];
                all.AddRange(notes.Value!.Select(n => new ServerNotification(
                    n.Id, connection.Id, connection.DisplayName, n.Type, n.CreatedAt, n.Actor,
                    n.SpaceId is null ? null : names.GetValueOrDefault(n.SpaceId, n.SpaceId), n.Path, n.Read)));
            }

            _current = [.. all.OrderByDescending(n => n.CreatedAt)];
            controller.HasNotifications = _current.Any(n => !n.Read);
            return _current;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        var connections = (await config.ListConnectionsAsync(ct)).ToDictionary(c => c.Id);
        foreach (var note in _current.Where(n => !n.Read))
        {
            if (connections.GetValueOrDefault(note.ConnectionId) is { } connection
                && await credentials.GetAsync(CredentialKeys.DeviceToken(connection.Id), ct) is { } token)
            {
                await server.MarkNotificationReadAsync(new ServerLogin(connection.ServerUrl, token, connection.PinnedCertificateSha256), note.Id, ct);
            }
        }

        await RefreshAsync(ct);
    }
}
