using SyncDrive.Core.Model;

namespace SyncDrive.Server.App.Ports;

public enum BlockWriteResult
{
    Stored,
    HashMismatch,
}

/// <summary>Inhaltsadressierter Blockspeicher. Ein Block ist unveränderlich; Schreiben desselben Blocks ist harmlos.</summary>
public interface IBlockStore
{
    /// <summary>Größe des Blocks oder null, wenn er fehlt.</summary>
    Task<long?> GetSizeAsync(ContentHash hash, CancellationToken ct);

    /// <summary>Speichert den Strom nur, wenn sein SHA-256 gleich <paramref name="expected"/> ist.</summary>
    Task<BlockWriteResult> WriteAsync(ContentHash expected, Stream content, CancellationToken ct);

    Task<Stream?> OpenReadAsync(ContentHash hash, CancellationToken ct);
}
