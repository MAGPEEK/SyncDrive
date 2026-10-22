using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Server.App.Spaces;

internal enum CommitOp
{
    Put,
    Mkdir,
    Delete,
    Move,
    Copy,
}

/// <summary>Geprüfte Fassung eines <see cref="CommitRequest"/>: Pfade und Hashes sind bereits typisiert.</summary>
internal sealed record CommitCommand(
    CommitOp Op,
    RelativePath Path,
    long BaseRevision,
    RelativePath? NewPath,
    IReadOnlyList<ContentHash> Blocks,
    long Size,
    DateTimeOffset MTime,
    ContentHash? FileHash)
{
    public static ServiceResult<CommitCommand> Parse(CommitRequest r)
    {
        if (r.BaseRevision < 0)
        {
            return Invalid(ErrorCode.InvalidRequest, "baseRevision darf nicht negativ sein.");
        }

        if (!TryParseOp(r.Op, out var op))
        {
            return Invalid(ErrorCode.InvalidRequest, $"Unbekannte Operation '{r.Op}'.");
        }

        if (!RelativePath.TryParse(r.Path, out var path) || path.IsRoot)
        {
            return Invalid(ErrorCode.InvalidPath, "Ungültiger Pfad.");
        }

        RelativePath? newPath = null;
        if (op is CommitOp.Move or CommitOp.Copy)
        {
            if (!RelativePath.TryParse(r.NewPath, out var parsedNew) || parsedNew.IsRoot)
            {
                return Invalid(ErrorCode.InvalidPath, "Ungültiges Ziel.");
            }

            newPath = parsedNew;
        }

        return op == CommitOp.Put
            ? ParsePut(r, path)
            : ServiceResult.Ok(new CommitCommand(
                op, path, r.BaseRevision, newPath, [], 0, r.MTime ?? DateTimeOffset.UnixEpoch, null));
    }

    private static ServiceResult<CommitCommand> ParsePut(CommitRequest r, RelativePath path)
    {
        if (r.Size < 0 || r.MTime is null || !ContentHash.TryParse(r.FileHash, out var fileHash))
        {
            return Invalid(ErrorCode.InvalidRequest, "size, mtime und fileHash sind für put nötig.");
        }

        var blocks = new List<ContentHash>();
        foreach (var text in r.Blocks ?? [])
        {
            if (!ContentHash.TryParse(text, out var block))
            {
                return Invalid(ErrorCode.InvalidRequest, $"Ungültiger Blockhash '{text}'.");
            }

            blocks.Add(block);
        }

        if ((r.Size == 0) != (blocks.Count == 0))
        {
            return Invalid(ErrorCode.InvalidRequest, "Leere Dateien haben keine Blöcke, andere mindestens einen.");
        }

        return ServiceResult.Ok(new CommitCommand(
            CommitOp.Put, path, r.BaseRevision, null, blocks, r.Size, r.MTime.Value, fileHash));
    }

    private static bool TryParseOp(string op, out CommitOp parsed)
    {
        (var ok, parsed) = op switch
        {
            CommitOps.Put => (true, CommitOp.Put),
            CommitOps.Mkdir => (true, CommitOp.Mkdir),
            CommitOps.Delete => (true, CommitOp.Delete),
            CommitOps.Move => (true, CommitOp.Move),
            CommitOps.Copy => (true, CommitOp.Copy),
            _ => (false, default),
        };
        return ok;
    }

    private static ServiceResult<CommitCommand> Invalid(string code, string message) =>
        ServiceResult.Fail<CommitCommand>(new ServiceError(code, message));
}
