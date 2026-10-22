using SyncDrive.Server.App.Ports;

namespace SyncDrive.Server.App.Accounts;

/// <summary>Kontingente aus den Benutzerkonten: ein Space zählt gegen das Kontingent seines Besitzers.</summary>
public sealed class AccountQuotas(IAccountStore store) : ISpaceQuotas
{
    public Task<long?> QuotaOfAsync(string spaceId, CancellationToken ct) => store.GetOwnerQuotaAsync(spaceId, ct);
}
