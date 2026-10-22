using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Filtering;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>Szenarien: mehrere Clients gegen die echte Server-Logik, echte Ordner auf der Platte.</summary>
public sealed class SyncScenarioTests : IDisposable
{
    private static readonly DateTime Earlier = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc);

    private readonly Cluster _cluster = new();

    public void Dispose() => _cluster.Dispose();

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task Files_and_folders_travel_from_one_client_to_another_with_content_and_mtime()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("Projekte/Plan.txt", "Plan", Earlier);
        a.Write("Projekte/Unter/Notiz.txt", "Notiz", Earlier);
        a.Write("readme.md", "# Hallo", Earlier);

        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Equal(a.Files(), b.Files());
        Assert.Equal(Earlier, File.GetLastWriteTimeUtc(b.Full("Projekte/Unter/Notiz.txt")));
    }

    [Fact]
    public async Task A_second_sync_without_changes_does_nothing()
    {
        var a = _cluster.AddClient("A");
        a.Write("x.txt", "x");
        await a.SyncOkAsync();
        var uploads = a.Remote.UploadedBlocks.Count;

        var report = await a.SyncOkAsync();

        Assert.All(report.Outcomes, o => Assert.Equal(OutcomeKind.Unchanged, o.Kind));
        Assert.Equal(uploads, a.Remote.UploadedBlocks.Count);
    }

    [Fact]
    public async Task An_edit_reaches_the_other_client()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("doc.txt", "v1", Earlier);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Write("doc.txt", "v2 – länger", Later);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Equal("v2 – länger", b.Read("doc.txt"));
    }

    [Fact]
    public async Task A_delete_reaches_the_other_client_whose_copy_goes_to_its_trash()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("old.txt", "wichtig");
        a.Write("Ordner/inner.txt", "innen");
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Delete("old.txt");
        a.Delete("Ordner");
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Empty(b.Files());
        Assert.Contains("wichtig", b.TrashFiles());
        Assert.Contains("innen", b.TrashFiles());
    }

    [Fact]
    public async Task Concurrent_edits_keep_both_versions_newer_one_at_the_original_path()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("x.txt", "basis", Earlier);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Write("x.txt", "von A (älter)", Earlier.AddMinutes(10));
        b.Write("x.txt", "von B (neuer)", Later);
        await a.SyncOkAsync();
        await b.SyncOkAsync(); // B ist neuer: Serverfassung (A) wird zur Konfliktkopie
        await a.SyncOkAsync();

        foreach (var client in new[] { a, b })
        {
            var files = client.Files();
            Assert.Equal("von B (neuer)", files["x.txt"]);
            Assert.Contains("von A (älter)", files.Values);
            Assert.Equal(2, files.Count);
        }

        Assert.Equal(a.Files(), b.Files());
    }

    [Fact]
    public async Task Concurrent_edits_where_the_local_one_is_older_keep_it_as_conflict_copy()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("x.txt", "basis", Earlier);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Write("x.txt", "von A (neuer)", Later);
        b.Write("x.txt", "von B (älter)", Earlier.AddMinutes(10));
        await a.SyncOkAsync();
        var report = await b.SyncOkAsync(); // B verliert: lokale Fassung wird umbenannt, Serverfassung geholt
        await a.SyncOkAsync();

        Assert.Contains(report.Outcomes, o => o.Kind == OutcomeKind.ConflictResolved && o.ConflictCopy is not null);
        Assert.Equal("von A (neuer)", b.Read("x.txt"));
        Assert.Contains("von B (älter)", a.Files().Values);
        Assert.Equal(a.Files(), b.Files());
        Assert.Contains(b.Log.Entries, e => e.Action == ActivityAction.Conflict);
    }

    [Fact]
    public async Task Server_wins_strategy_keeps_the_local_edit_as_conflict_copy()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B", new ClientSettings { Conflict = ConflictStrategy.ServerWins });
        a.Write("x.txt", "basis", Earlier);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Write("x.txt", "Server", Earlier.AddMinutes(1));
        b.Write("x.txt", "Lokal, aber neuer", Later);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Equal("Server", b.Read("x.txt"));
        Assert.Contains("Lokal, aber neuer", b.Files().Values);
    }

    [Fact]
    public async Task Both_clients_creating_the_same_new_file_keep_both_contents()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("neu.txt", "A", Earlier);
        b.Write("neu.txt", "B", Later);

        await a.SyncOkAsync();
        await b.SyncOkAsync();
        await a.SyncOkAsync();

        Assert.Equal(["A", "B"], a.Files().Values.Order());
        Assert.Equal(a.Files(), b.Files());
    }

    [Fact]
    public async Task An_edit_beats_a_concurrent_delete_on_every_client()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("x.txt", "basis", Earlier);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Delete("x.txt");
        b.Write("x.txt", "geändert", Later);
        await a.SyncOkAsync();
        await b.SyncOkAsync();
        await a.SyncOkAsync();

        Assert.Equal("geändert", a.Read("x.txt"));
        Assert.Equal("geändert", b.Read("x.txt"));
    }

    [Fact]
    public async Task On_startup_files_deleted_while_offline_are_fetched_again_by_default()
    {
        var a = _cluster.AddClient("A");
        a.Write("x.txt", "bleibt");
        await a.SyncOkAsync();

        a.Delete("x.txt");
        await a.SyncOkAsync(startup: true);

        Assert.Equal("bleibt", a.Read("x.txt"));
    }

    [Fact]
    public async Task On_startup_offline_deletes_are_propagated_when_configured()
    {
        var a = _cluster.AddClient("A", new ClientSettings { OnRestart = OnRestartLocalDeletes.PropagateDelete });
        var b = _cluster.AddClient("B");
        a.Write("x.txt", "weg");
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Delete("x.txt");
        await a.SyncOkAsync(startup: true);
        await b.SyncOkAsync();

        Assert.False(b.Exists("x.txt"));
    }

    [Fact]
    public async Task Deletes_seen_while_running_are_propagated()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("x.txt", "weg");
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Delete("x.txt");
        await a.SyncOkAsync(startup: false);
        await b.SyncOkAsync();

        Assert.False(b.Exists("x.txt"));
    }

    [Fact]
    public async Task Filtered_files_stay_local_and_are_logged_as_unsynced()
    {
        var a = _cluster.AddClient("A", rules: [new ExtensionRule(["tmp"])]);
        var b = _cluster.AddClient("B");
        a.Write("~$Bericht.tmp", "Office-Sperrdatei");
        a.Write("Bericht.txt", "echt");

        var report = await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Equal(["Bericht.txt"], b.Files().Keys);
        Assert.Contains(report.Outcomes, o => o is { Kind: OutcomeKind.Skipped, SkipReason: FilterReason.Extension });
        Assert.Contains(a.Log.Entries, e => e is { Action: ActivityAction.Upload, Result: ActivityResult.Unsynced, Reason: "Extension" });
        Assert.True(a.Exists("~$Bericht.tmp"));
    }

    [Theory]
    [InlineData(false, "tresor.bin", false)] // Größe und mtime gleich: Inhalt gilt als unverändert (schwache Prüfung)
    [InlineData(true, "tresor.bin", true)]   // erweiterte Konsistenzprüfung: immer hashen
    [InlineData(false, "tresor.tc", true)]   // Endung mit Hash-Pflicht (Container ändern sich ohne neue mtime)
    public async Task Content_changes_that_keep_size_and_mtime_are_found_only_when_hashing_is_forced(
        bool advancedCheck, string name, bool expectUpload)
    {
        var a = _cluster.AddClient("A", configure: c => c with { AdvancedConsistencyCheck = advancedCheck });
        var b = _cluster.AddClient("B");
        a.Write(name, "AAAA", Earlier);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        a.Write(name, "BBBB", Earlier); // gleiche Größe, gleiche mtime
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Equal(expectUpload ? "BBBB" : "AAAA", b.Read(name));
    }

    [Fact]
    public async Task Identical_content_is_uploaded_only_once()
    {
        var a = _cluster.AddClient("A");
        var content = RandomBytes(40_000, 1);
        a.WriteBytes("eins.bin", content);
        await a.SyncOkAsync();
        var afterFirst = a.Remote.UploadedBlocks.Count;

        a.WriteBytes("Kopie/zwei.bin", content);
        await a.SyncOkAsync();

        Assert.True(afterFirst > 5);
        Assert.Equal(afterFirst, a.Remote.UploadedBlocks.Count);
    }

    [Fact]
    public async Task Changing_a_few_bytes_in_a_large_file_uploads_only_a_few_blocks()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        var content = RandomBytes(200_000, 2);
        a.WriteBytes("gross.bin", content, Earlier);
        await a.SyncOkAsync();
        var before = a.Remote.UploadedBlocks.Count;

        content[100_000] ^= 0xFF;
        content[100_001] ^= 0xFF;
        a.WriteBytes("gross.bin", content, Later);
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.InRange(a.Remote.UploadedBlocks.Count - before, 1, 3);
        Assert.Equal(content, File.ReadAllBytes(b.Full("gross.bin")));
    }

    [Fact]
    public async Task An_interrupted_upload_resumes_with_only_the_missing_blocks()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.WriteBytes("video.bin", RandomBytes(150_000, 3));
        a.Remote.FailUploadsAfter = 20;

        var interrupted = await a.SyncAsync();
        Assert.Contains(interrupted.Failures, f => f.Error.Code == RemoteErrors.Network);
        Assert.Equal(20, a.Remote.UploadedBlocks.Count);

        a.Remote.FailUploadsAfter = null;
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        var uploads = a.Remote.UploadedBlocks.ToList();
        Assert.Equal(uploads.Count, uploads.Distinct().Count()); // kein Block zweimal gesendet
        Assert.True(uploads.Count > 20);
        Assert.Equal(File.ReadAllBytes(a.Full("video.bin")), File.ReadAllBytes(b.Full("video.bin")));
    }

    [Fact]
    public async Task Editing_a_file_someone_else_locked_keeps_the_edit_as_a_new_file()
    {
        var a = _cluster.AddClient("A");
        a.Write("vertrag.docx", "Fassung der Kollegin", Earlier);
        await a.SyncOkAsync();
        await _cluster.Service.LockAsync(Cluster.Space, "vertrag.docx", "u_kollegin", CancellationToken.None);

        a.Write("vertrag.docx", "meine Änderung", Later);
        var report = await a.SyncOkAsync();

        Assert.Contains(report.Outcomes, o => o.Kind == OutcomeKind.ConflictResolved);
        Assert.Equal("Fassung der Kollegin", a.Read("vertrag.docx"));
        var copy = Assert.Single(a.Files(), f => f.Value == "meine Änderung").Key;
        Assert.NotNull((await _cluster.Service.GetNodeAsync(Cluster.Space, copy, CancellationToken.None)).Value); // Kopie ist auf dem Server
        Assert.Equal("u_kollegin", (await _cluster.Service.GetNodeAsync(Cluster.Space, "vertrag.docx", CancellationToken.None)).Value!.Node.LockedBy);
    }

    [Fact]
    public async Task Empty_files_and_empty_folders_are_synced()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("leer.txt", string.Empty);
        Directory.CreateDirectory(a.Full("Leerer Ordner"));

        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Equal(string.Empty, b.Read("leer.txt"));
        Assert.True(Directory.Exists(b.Full("Leerer Ordner")));
    }

    [Fact]
    public async Task Without_network_nothing_changes_and_the_next_sync_catches_up()
    {
        var a = _cluster.AddClient("A");
        a.Write("x.txt", "offline geschrieben");
        a.Remote.Offline = true;

        var offline = await a.SyncAsync();

        Assert.NotEmpty(offline.Failures);
        Assert.All(offline.Failures, f => Assert.Equal(RemoteErrors.Network, f.Error.Code));
        Assert.Empty(a.State.All);

        a.Remote.Offline = false;
        await a.SyncOkAsync();
        Assert.Single(a.State.All);
    }

    [Theory]
    [InlineData(true, true)]   // Standard: lokal Gelöschtes bleibt auf dem Server
    [InlineData(false, false)] // abgewählt: Löschung wird hochgeladen
    public async Task Upload_only_tasks_keep_locally_deleted_files_on_the_server_by_default(bool keep, bool stillOnServer)
    {
        var a = _cluster.AddClient("A", direction: Core.Merge.SyncDirection.UploadOnly,
            configure: c => c with { KeepLocallyDeletedOnServer = keep });
        var b = _cluster.AddClient("B");
        a.Write("bericht.txt", "hochgeladen");
        await a.SyncOkAsync();

        a.Delete("bericht.txt");
        await a.SyncOkAsync();
        await a.SyncOkAsync(startup: true); // auch ein Neustart holt bei „nur hochladen“ nichts zurück
        await b.SyncOkAsync();

        Assert.Equal(stillOnServer, b.Exists("bericht.txt"));
        Assert.False(a.Exists("bericht.txt"));
    }

    [Fact]
    public async Task Download_only_tasks_never_upload_local_changes()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B", direction: Core.Merge.SyncDirection.DownloadOnly);
        a.Write("server.txt", "vom Server");
        await a.SyncOkAsync();

        b.Write("nur-lokal.txt", "bleibt hier");
        await b.SyncOkAsync();
        await a.SyncOkAsync();

        Assert.Equal("vom Server", b.Read("server.txt"));
        Assert.False(a.Exists("nur-lokal.txt"));
    }
}
