using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// Echtzeit der Weboberfläche: ein einziger Long-Poll für das ganze Konto (<c>account/changes/wait</c>). Ändert sich etwas, das der
/// Benutzer sieht – Dateien in einem seiner Spaces, Freigaben, Benachrichtigungen, Favoriten oder Labels, auch von anderen Geräten –,
/// lädt die Oberfläche neu, was sie gerade zeigt. Änderungen kurz hintereinander (ein Ordner voller Dateien) fasst <see cref="Settle"/>
/// zu einem Neuladen zusammen. Fehler (kurz offline) bleiben still; nur eine abgelaufene Sitzung meldet sich ab.
/// </summary>
public sealed class LiveUpdatesViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    public async Task RunAsync(Func<Task> reload, CancellationToken ct)
    {
        string? stamp = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var next = await api.WaitForAccountChangesAsync(stamp, ct);
                if (stamp is null || next == stamp)
                {
                    stamp = next; // erster Stand oder Zeit abgelaufen
                    continue;
                }

                await Task.Delay(Settle, ct);
                stamp = await api.WaitForAccountChangesAsync(null, ct); // was in der Zwischenzeit kam, lädt dasselbe Neuladen mit
                await reload();
            }
            catch (DriveException e) when (e.Code == ErrorCode.Unauthorized)
            {
                OnSignedOut();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is DriveException or HttpRequestException)
            {
                try
                {
                    await Task.Delay(RetryAfter, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
