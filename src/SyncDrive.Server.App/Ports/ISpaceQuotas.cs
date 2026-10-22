namespace SyncDrive.Server.App.Ports;

/// <summary>Speicherkontingent eines Space (M7): das seines Besitzers. Null = unbegrenzt.</summary>
public interface ISpaceQuotas
{
    Task<long?> QuotaOfAsync(string spaceId, CancellationToken ct);
}
