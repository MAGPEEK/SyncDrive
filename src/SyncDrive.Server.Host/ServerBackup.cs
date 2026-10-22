using System.Globalization;
using Microsoft.Extensions.Options;
using SyncDrive.Infra.Blob;
using SyncDrive.Infra.Sqlite;

namespace SyncDrive.Server.Host;

/// <summary>
/// Sicherung der Server-Daten im laufenden Betrieb nach <see cref="ServerOptions.BackupDirectory"/>:
/// <c>db/server-JJJJMMTT-HHMMSS.db</c> (konsistenter Stand, die letzten <see cref="ServerOptions.BackupsToKeep"/>), <c>blocks/</c>
/// (inkrementell, gemeinsam für alle Stände), <c>portal/</c> und <c>avatars/</c>. Wiederherstellen: Server anhalten, einen Stand als <c>server.db</c> und die
/// Ordner <c>blocks/</c>, <c>portal/</c> und <c>avatars/</c> nach <c>/data</c> kopieren, Server starten.
/// </summary>
public sealed class ServerBackup(IOptions<ServerOptions> options, TimeProvider clock)
{
    private const string DatabaseFolder = "db";

    /// <returns>Name des neuen Datenbank-Stands und Anzahl neu kopierter Blöcke.</returns>
    public async Task<(string Snapshot, int NewBlocks)> RunAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var target = settings.BackupDirectory ?? throw new InvalidOperationException("Kein Sicherungsziel konfiguriert.");
        var databases = Path.Combine(target, DatabaseFolder);
        Directory.CreateDirectory(databases);

        var name = "server-" + clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".db";
        await SqliteBackup.CreateSnapshotAsync(Path.Combine(settings.DataDirectory, "server.db"), Path.Combine(databases, name), ct);

        // Blöcke nach der Datenbank: jeder Block, auf den der Stand verweist, lag vorher schon im Speicher (erst hochladen, dann committen).
        var newBlocks = await new FileBlockStore(Path.Combine(settings.DataDirectory, "blocks")).CopyMissingToAsync(Path.Combine(target, "blocks"), ct);
        CopyFolder(Path.Combine(settings.DataDirectory, "portal"), Path.Combine(target, "portal"));
        CopyFolder(Path.Combine(settings.DataDirectory, "avatars"), Path.Combine(target, "avatars"));
        Prune(databases, settings.BackupsToKeep);
        return (name, newBlocks);
    }

    /// <summary>Nächster Sicherungszeitpunkt nach <paramref name="now"/> zur Uhrzeit <paramref name="time"/> (Ortszeit von <paramref name="zone"/>).</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeOnly time, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date + time.ToTimeSpan();
        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }

    private static void CopyFolder(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void Prune(string databases, int keep)
    {
        var snapshots = Directory.GetFiles(databases, "server-*.db").Order(StringComparer.Ordinal).ToList();
        foreach (var old in snapshots.Take(Math.Max(0, snapshots.Count - Math.Max(1, keep))))
        {
            File.Delete(old);
        }
    }
}

/// <summary>Startet <see cref="ServerBackup"/> täglich zur eingestellten Uhrzeit; ohne Sicherungsziel tut er nichts.</summary>
public sealed partial class BackupScheduler(ServerBackup backup, IOptions<ServerOptions> options, TimeProvider clock, ILogger<BackupScheduler> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.BackupDirectory))
        {
            return;
        }

        if (!TimeOnly.TryParse(settings.BackupTime, CultureInfo.InvariantCulture, out var time))
        {
            LogBadTime(logger, settings.BackupTime);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = ServerBackup.NextRun(clock.GetUtcNow(), time, TimeZoneInfo.Local);
            await Task.Delay(next - clock.GetUtcNow(), clock, stoppingToken);
            try
            {
                var (snapshot, blocks) = await backup.RunAsync(stoppingToken);
                LogDone(logger, snapshot, blocks);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                LogFailed(logger, e); // nächster Versuch am nächsten Tag; der Server läuft weiter
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sicherung fertig: {Snapshot}, {Blocks} neue Blöcke.")]
    private static partial void LogDone(ILogger logger, string snapshot, int blocks);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sicherung fehlgeschlagen.")]
    private static partial void LogFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sicherungszeit „{Time}“ ist keine Uhrzeit (Format HH:MM); keine Sicherung.")]
    private static partial void LogBadTime(ILogger logger, string time);
}
