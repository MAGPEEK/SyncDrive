using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Results;
using SyncDrive.Infra.Fs;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>Zwei laufende Clients (Watcher, Feed-Long-Poll, Warteschlange) ohne manuelles Anstoßen: so, wie der Daemon später läuft.</summary>
public sealed class RunnerScenarioTests : IDisposable
{
    private static readonly SyncRunnerOptions Fast = new(
        SettleDelay: TimeSpan.FromMilliseconds(100),
        LongPoll: TimeSpan.FromSeconds(5),
        RescanInterval: TimeSpan.FromMinutes(10),
        Parallelism: 2,
        MaxBackoff: TimeSpan.FromSeconds(2));

    private readonly Cluster _cluster = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(FileSystemChangeSource Source, Task Run)> _running = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var (source, run) in _running)
        {
            try
            {
                run.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // Abbruch beim Beenden ist erwartet
            }

            source.Dispose();
        }

        _cluster.Dispose();
        _stop.Dispose();
    }

    private SyncRunner Start(TestClient client, bool paused = false)
    {
        var source = new FileSystemChangeSource(client.Root);
        var name = client.Root[^8..];
        var runner = new SyncRunner(
            client.Session, new RecordingChangeSource(source, _events, name), Fast with { OnError = (p, e) => _errors.Enqueue($"{name} {p}: {e.Code} {e.Message}") },
            observer: client.Tracker);
        if (paused)
        {
            runner.Pause();
        }

        _running.Add((source, Task.Run(() => runner.RunAsync(_stop.Token))));
        return runner;
    }

    private async Task EventuallyAsync(Func<bool> condition, string what, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Zeitüberschreitung: {what}. {detail?.Invoke()} Fehler: {string.Join(" | ", _errors.Take(10))}. Ereignisse: {string.Join(" | ", _events.Where(e => e.Contains("Moved", StringComparison.Ordinal) || !e.Contains(".txt", StringComparison.Ordinal)))}. Runner: {string.Join(", ", _running.Select(r => r.Run.Status + " " + r.Run.Exception?.GetBaseException().Message))}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task A_file_written_on_one_running_client_appears_on_the_other_and_a_delete_travels_back()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("vorher.txt", "schon da");
        Start(a);
        Start(b);
        await EventuallyAsync(() => b.Exists("vorher.txt"), "Start-Abgleich bringt vorhandene Datei zu B");

        a.Write("Live/neu.txt", "live übertragen");
        await EventuallyAsync(() => b.Exists("Live/neu.txt") && b.Read("Live/neu.txt") == "live übertragen", "neue Datei erreicht B");

        b.Delete("Live/neu.txt");
        await EventuallyAsync(() => !a.Exists("Live/neu.txt"), "Löschung von B erreicht A");
        Assert.Contains("live übertragen", a.TrashFiles());
    }

    [Fact]
    public async Task A_folder_renamed_while_running_is_moved_on_the_server_and_followed_without_downloads()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        for (var i = 0; i < 10; i++)
        {
            a.Write($"Alt/f{i}.txt", $"{i}");
        }

        Start(a);
        Start(b);
        await EventuallyAsync(() => b.Files().Count == 10, "Start-Abgleich");
        var downloads = b.Log.Entries.Count(e => e.Action == App.Ports.ActivityAction.Download);

        Directory.Move(a.Full("Alt"), a.Full("Neu"));

        await EventuallyAsync(() => !b.Exists("Alt") && b.Files().Keys.Count(k => k.StartsWith("Neu/", StringComparison.Ordinal)) == 10, "Umbenennung erreicht B", () => $"B-Dateien: {string.Join(",", b.Files().Keys)} B-Ordner: {string.Join(",", Directory.GetDirectories(b.Root).Select(Path.GetFileName))} A: {a.Tracker.Snapshot} B: {b.Tracker.Snapshot} B-Papierkorb: {b.TrashFiles().Count()} B-Log: {string.Join(",", b.Log.Entries.Select(e => e.Action + " " + e.Path + " " + e.Result + " " + e.Reason))}");
        Assert.True(a.Remote.Commits.Any(c => c.Op == Contracts.CommitOps.Move), "Commits: " + string.Join(", ", a.Remote.Commits.Select(c => $"{c.Op} {c.Path}{(c.NewPath is null ? "" : " -> " + c.NewPath)}")) + " | Fehler: " + string.Join(" | ", _errors) + " | Ereignisse: " + string.Join(" | ", _events));
        Assert.Equal(downloads, b.Log.Entries.Count(e => e.Action == App.Ports.ActivityAction.Download));
        Assert.Empty(b.TrashFiles());
    }

    [Fact]
    public async Task A_populated_folder_moved_in_from_outside_arrives_with_all_its_files()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        var outside = Path.Combine(Path.GetTempPath(), "syncdrive-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "tief"));
        File.WriteAllText(Path.Combine(outside, "eins.txt"), "1");
        File.WriteAllText(Path.Combine(outside, "tief", "zwei.txt"), "2");
        Start(a);
        Start(b);
        await Task.Delay(500);

        Directory.Move(outside, a.Full("Importiert"));

        await EventuallyAsync(() => b.Exists("Importiert/tief/zwei.txt") && b.Exists("Importiert/eins.txt"), "Inhalt des neuen Ordners erreicht B");
    }

    [Fact]
    public async Task A_watcher_overflow_triggers_a_rescan_that_finds_the_missed_change()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        using var manual = new ManualChangeSource();
        var runner = new SyncRunner(a.Session, manual, Fast);
        _running.Add((new FileSystemChangeSource(a.Root), Task.Run(() => runner.RunAsync(_stop.Token))));
        Start(b);
        await Task.Delay(500);

        a.Write("verpasst.txt", "kein Ereignis dafür");   // der Watcher „verliert“ das Ereignis
        manual.Emit(new App.Ports.LocalChange.Overflow());

        await EventuallyAsync(() => b.Exists("verpasst.txt"), "Rescan nach Überlauf findet die Datei");
    }

    /// <summary>Reicht die Ereignisse des Watchers durch und schreibt sie für Fehlermeldungen mit.</summary>
    private sealed class RecordingChangeSource(
        App.Ports.ILocalChangeSource inner, System.Collections.Concurrent.ConcurrentQueue<string> log, string name) : App.Ports.ILocalChangeSource
    {
        public async IAsyncEnumerable<App.Ports.LocalChange> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var change in inner.ReadAllAsync(ct))
            {
                log.Enqueue($"{DateTime.UtcNow:ss.fff} {name} {change}");
                yield return change;
            }
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Änderungsquelle, die der Test selbst füttert.</summary>
    private sealed class ManualChangeSource : App.Ports.ILocalChangeSource
    {
        private readonly System.Threading.Channels.Channel<App.Ports.LocalChange> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<App.Ports.LocalChange>();

        public void Emit(App.Ports.LocalChange change) => _channel.Writer.TryWrite(change);

        public IAsyncEnumerable<App.Ports.LocalChange> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

        public void Dispose() => _channel.Writer.TryComplete();
    }

    [Fact]
    public async Task Pausing_holds_back_new_changes_and_resuming_syncs_them_status_follows()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        var runnerA = Start(a);
        Start(b);
        await EventuallyAsync(() => a.Tracker.Snapshot.State == App.Status.TaskState.Done, "A ist verbunden und fertig");

        runnerA.Pause();
        a.Write("während-pause.txt", "wartet");
        await Task.Delay(800);

        Assert.Equal(App.Status.TaskState.Paused, a.Tracker.Snapshot.State);
        Assert.False(b.Exists("während-pause.txt"));

        runnerA.Resume();
        await EventuallyAsync(() => b.Exists("während-pause.txt"), "nach Fortsetzen kommt die Datei an");
        await EventuallyAsync(() => a.Tracker.Snapshot.State == App.Status.TaskState.Done, "A wieder fertig");
    }

    [Fact]
    public async Task Pausing_before_the_start_holds_back_the_initial_sync_too()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("vorhanden.txt", "liegt schon da");
        Start(b);

        var runnerA = Start(a, paused: true);
        await Task.Delay(800);

        Assert.Empty(a.Remote.Commits);
        Assert.Equal(App.Status.TaskState.Paused, a.Tracker.Snapshot.State);

        runnerA.Resume();
        await EventuallyAsync(() => b.Exists("vorhanden.txt"), "nach Fortsetzen läuft der Erstabgleich");
    }

    [Fact]
    public async Task A_revoked_or_missing_space_stops_the_runner_with_a_fatal_error()
    {
        var a = _cluster.AddClient("A");
        var source = new FileSystemChangeSource(a.Root);
        var runner = new SyncRunner(a.Session, source, Fast);
        _cluster.Store.RemoveSpace(Cluster.Space);

        var stopped = await Assert.ThrowsAsync<SyncStoppedException>(() => runner.RunAsync(_stop.Token)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SyncErrorKind.Fatal, stopped.Error.Kind);
        source.Dispose();
    }
}
