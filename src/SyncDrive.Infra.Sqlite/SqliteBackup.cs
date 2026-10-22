namespace SyncDrive.Infra.Sqlite;

/// <summary>Konsistente Kopie der Server-Datenbank im laufenden Betrieb (<c>VACUUM INTO</c>: ein Stand, kompakt, ohne WAL-Dateien).</summary>
public static class SqliteBackup
{
    public static async Task CreateSnapshotAsync(string databasePath, string targetPath, CancellationToken ct)
    {
        var temp = targetPath + ".partial";
        File.Delete(temp);
        var connectionString = SqliteDatabase.Initialize(databasePath);
        await using (var connection = await SqliteDatabase.OpenAsync(connectionString, ct))
        await using (var command = SqliteDatabase.Command(connection, "VACUUM INTO $target", ("$target", temp)))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        File.Move(temp, targetPath, overwrite: true);
    }
}
