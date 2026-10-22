using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>Administratoren gestalten die Anmeldeseite: Servername, Text darunter, eigenes Hintergrundbild (hochladen oder entfernen).</summary>
public sealed class PortalViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const long MaxBackgroundBytes = 5 * 1024 * 1024;

    public PortalInfo? Portal { get; private set; }

    public bool IsBusy { get; private set; }

    /// <summary>Nach jeder erfolgreichen Änderung, damit die Sitzung die neue Anmeldeseite kennt.</summary>
    public event Action<PortalInfo>? Saved;

    public async Task LoadAsync(CancellationToken ct)
    {
        DismissMessage();
        await RunAsync(async () => Portal = await api.GetPortalAsync(ct));
        OnChanged();
    }

    public Task<bool> SaveAsync(string serverName, string? loginMessage, CancellationToken ct) =>
        ChangeAsync(() => api.UpdatePortalAsync(serverName, loginMessage, ct));

    public Task<bool> SetBackgroundAsync(UploadFile image, CancellationToken ct) =>
        image.Size > MaxBackgroundBytes
            ? Task.FromResult(Fail(Texts.ImageTooLarge))
            : ChangeAsync(async () =>
            {
                await using var content = image.Open();
                return await api.SetPortalBackgroundAsync(content, ct);
            });

    public Task<bool> RemoveBackgroundAsync(CancellationToken ct) => ChangeAsync(() => api.RemovePortalBackgroundAsync(ct));

    private async Task<bool> ChangeAsync(Func<Task<PortalInfo>> change)
    {
        IsBusy = true;
        OnChanged();
        PortalInfo? updated = null;
        var ok = await RunAsync(async () => updated = await change());
        IsBusy = false;
        if (ok)
        {
            Portal = updated;
            Saved?.Invoke(updated!);
            Inform(Texts.Saved);
        }

        OnChanged();
        return ok;
    }
}
