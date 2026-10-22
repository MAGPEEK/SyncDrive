using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Contracts;
using SyncDrive.Core.Filtering;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;
using SyncDrive.Infra.Fs;
using SyncDrive.Testing;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>
/// On-Demand (A12) gegen den echten Server-Kern, mit einem Ordner, der Platzhalter nachbildet (<see cref="FakeCloudStore"/>): Serverdateien
/// kommen ohne Inhalt an, der Sync liest Platzhalter nie, Öffnen holt den geprüften Inhalt, Statussymbole folgen dem Ergebnis jeder Bewertung.
/// </summary>
public sealed class OnDemandTests : IDisposable
{
    private readonly Cluster _cluster = new();
    private readonly TestClient _a;
    private readonly OnDemandClient _b;

    public OnDemandTests()
    {
        _a = _cluster.AddClient("A");
        _b = new OnDemandClient(_cluster, "B");
    }

    public void Dispose()
    {
        _b.Dispose();
        _cluster.Dispose();
    }

    [Fact]
    public async Task Server_files_arrive_as_placeholders_without_content()
    {
        _a.Write("docs/a.txt", "Inhalt von A");
        await _a.SyncOkAsync();

        await _b.SyncOkAsync();

        var entry = await _b.Cloud.StatAsync(RelativePath.Parse("docs/a.txt"), default);
        Assert.True(entry is { IsCloudOnly: true, Size: 12 });
        Assert.Equal(0, _b.Cloud.ReadsOfPlaceholders);
        Assert.True(_b.Cloud.InSync["docs/a.txt"]);
        Assert.True(_b.Cloud.InSync["docs"]);
    }

    [Fact]
    public async Task Opening_a_placeholder_fetches_the_verified_content_and_changes_nothing_on_the_server()
    {
        _a.Write("a.txt", "Inhalt von A");
        await _a.SyncOkAsync();
        await _b.SyncOkAsync();
        var commits = _b.Remote.Commits.Count;

        Assert.True((await _b.Cloud.HydrateAsync("a.txt", _b.Hydrator)).IsSuccess);
        await _b.SyncOkAsync();

        Assert.Equal("Inhalt von A", _b.Read("a.txt"));
        Assert.False((await _b.Cloud.StatAsync(RelativePath.Parse("a.txt"), default))!.IsCloudOnly);
        Assert.Equal(commits, _b.Remote.Commits.Count);
    }

    [Fact]
    public async Task A_server_change_replaces_a_placeholder_by_a_new_placeholder()
    {
        _a.Write("a.txt", "eins");
        await _a.SyncOkAsync();
        await _b.SyncOkAsync();

        _a.Write("a.txt", "zwei, länger", DateTime.UtcNow.AddMinutes(1));
        await _a.SyncOkAsync();
        await _b.SyncOkAsync();

        var entry = await _b.Cloud.StatAsync(RelativePath.Parse("a.txt"), default);
        Assert.True(entry is { IsCloudOnly: true, Size: 13 });
        Assert.True((await _b.Cloud.HydrateAsync("a.txt", _b.Hydrator)).IsSuccess);
        Assert.Equal("zwei, länger", _b.Read("a.txt"));
    }

    [Fact]
    public async Task A_server_change_of_a_fetched_file_downloads_the_new_content()
    {
        _a.Write("a.txt", "eins");
        await _a.SyncOkAsync();
        await _b.SyncOkAsync();
        await _b.Cloud.HydrateAsync("a.txt", _b.Hydrator);

        _a.Write("a.txt", "zwei", DateTime.UtcNow.AddMinutes(1));
        await _a.SyncOkAsync();
        await _b.SyncOkAsync();

        Assert.Equal("zwei", _b.Read("a.txt"));
        Assert.False((await _b.Cloud.StatAsync(RelativePath.Parse("a.txt"), default))!.IsCloudOnly);
    }

    [Fact]
    public async Task Local_files_upload_as_usual_and_are_marked_in_sync()
    {
        _b.Write("neu.txt", "von B");

        await _b.SyncOkAsync();
        await _a.SyncOkAsync();

        Assert.Equal("von B", _a.Read("neu.txt"));
        Assert.True(_b.Cloud.InSync["neu.txt"]);
    }

    [Fact]
    public async Task A_placeholder_that_no_longer_matches_the_server_does_not_open()
    {
        _a.Write("a.txt", "eins");
        await _a.SyncOkAsync();
        await _b.SyncOkAsync();
        _a.Write("a.txt", "zwei", DateTime.UtcNow.AddMinutes(1));
        await _a.SyncOkAsync();

        var result = await _b.Cloud.HydrateAsync("a.txt", _b.Hydrator);

        Assert.Equal("placeholder_stale", result.Error?.Code);
        Assert.Equal(0, _b.Cloud.WrittenBytes("a.txt"));
    }

    [Fact]
    public async Task A_renamed_placeholder_becomes_a_server_move_without_fetching_it()
    {
        _a.Write("alt.txt", "Inhalt");
        await _a.SyncOkAsync();
        await _b.SyncOkAsync();

        _b.Cloud.MoveOnDisk("alt.txt", "neu.txt");
        await _b.SyncOkAsync();
        await _a.SyncOkAsync();

        Assert.Equal(new Dictionary<string, string> { ["neu.txt"] = "Inhalt" }, _a.Files());
        Assert.Equal([CommitOps.Move], _b.Remote.Commits.Select(c => c.Op));
        Assert.Equal(0, _b.Cloud.ReadsOfPlaceholders);
    }

    [Fact]
    public async Task A_placeholder_never_wins_against_the_server_even_without_a_base()
    {
        _a.Write("a.txt", "Serverfassung");
        await _a.SyncOkAsync();
        await _b.Cloud.CreateAsync(RelativePath.Parse("a.txt"), 99, DateTimeOffset.UtcNow.AddDays(-1), ContentHash.FromBytes(new byte[32]), default);

        await _b.SyncOkAsync();

        Assert.True((await _b.Cloud.HydrateAsync("a.txt", _b.Hydrator)).IsSuccess);
        Assert.Equal("Serverfassung", _b.Read("a.txt"));
        Assert.Empty(_b.Remote.Commits); // keine Konfliktkopie, kein Hochladen
    }

    [Fact]
    public async Task Without_on_demand_the_same_folder_downloads_everything()
    {
        using var full = new OnDemandClient(_cluster, "C", onDemand: false);
        _a.Write("a.txt", "Inhalt");
        await _a.SyncOkAsync();

        await full.SyncOkAsync();

        Assert.Equal("Inhalt", full.Read("a.txt"));
        Assert.True(full.Cloud.InSync["a.txt"]); // Statussymbole gibt es trotzdem
    }
}

/// <summary>Ein Client mit Platzhalter-Ordner und Statusbeobachter, wie ihn der Daemon für eine On-Demand-Aufgabe zusammensetzt.</summary>
internal sealed class OnDemandClient : IDisposable
{
    public OnDemandClient(Cluster cluster, string device, bool onDemand = true)
    {
        Root = Path.Combine(Path.GetTempPath(), $"syncdrive-od-{device}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Cloud = new FakeCloudStore(Root);
        Remote = new InProcessRemoteApi(cluster.Service, Cluster.Space);
        var remote = Remote;
        var config = new SyncTaskConfig(device, SyncDirection.TwoWay, new ClientSettings(), new FilterEngine([]), Cluster.SmallChunks, OnDemand: onDemand);
        var engine = new SyncEngine(Cloud, remote, State, config, new RecordingActivityLog(), TimeProvider.System, Cloud);
        Session = new SyncSession(engine, Cloud, remote, State, new ObserverGroup(new TaskStatusTracker(device), new PlaceholderStatusObserver(Cloud)));
        Hydrator = new Hydrator(remote);
    }

    public string Root { get; }

    public FakeCloudStore Cloud { get; }

    public InProcessRemoteApi Remote { get; }

    public InMemorySessionState State { get; } = new();

    public SyncSession Session { get; }

    public Hydrator Hydrator { get; }

    public async Task SyncOkAsync()
    {
        var report = await Session.RescanAsync(RelativePath.Root, false, CancellationToken.None);
        Assert.True(report.Failures.Count == 0, string.Join("; ", report.Failures.Select(f => $"{f.Path}: {f.Error.Code} {f.Error.Message}")));
    }

    public void Write(string relative, string content)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

/// <summary>
/// Ordner mit nachgebildeten Platzhaltern: Auf der Platte liegt eine leere Datei mit dem Datum der Serverfassung, Größe und Fassung merkt sich
/// der Fake. Lesen eines Platzhalters scheitert (und wird gezählt) wie unter Windows mit blockiertem eigenem Abruf. Schreibt jemand die Datei,
/// ist sie kein Platzhalter mehr.
/// </summary>
internal sealed class FakeCloudStore(string root) : ILocalStore, IPlaceholders
{
    private readonly LocalFileStore _files = new(root);
    private readonly ConcurrentDictionary<string, (long Size, DateTimeOffset MTime, ContentHash Hash)> _placeholders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _written = new(StringComparer.OrdinalIgnoreCase);
    private int _reads;

    public ConcurrentDictionary<string, bool> InSync { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ReadsOfPlaceholders => _reads;

    public long WrittenBytes(string path) => _written.GetValueOrDefault(path);

    public async IAsyncEnumerable<ScanItem> ScanAsync(RelativePath scanRoot, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _files.ScanAsync(scanRoot, ct))
        {
            yield return item is ScanItem.Found found ? new ScanItem.Found(Adjust(found.Entry)) : item;
        }
    }

    public async Task<LocalEntry?> StatAsync(RelativePath path, CancellationToken ct) =>
        await _files.StatAsync(path, ct) is { } entry ? Adjust(entry) : null;

    public async Task<Stream> OpenReadAsync(RelativePath path, CancellationToken ct)
    {
        if (await StatAsync(path, ct) is { IsCloudOnly: true })
        {
            Interlocked.Increment(ref _reads);
            throw new IOException("Platzhalter: eigener Abruf blockiert");
        }

        return await _files.OpenReadAsync(path, ct);
    }

    public Task<IStagedFile> StageAsync(RelativePath target, CancellationToken ct) => _files.StageAsync(target, ct);

    public Task CreateDirectoryAsync(RelativePath path, DateTimeOffset mtime, CancellationToken ct) => _files.CreateDirectoryAsync(path, mtime, ct);

    public Task<TrashItem?> MoveToTrashAsync(RelativePath path, CancellationToken ct)
    {
        _placeholders.TryRemove(path.Value, out _);
        return _files.MoveToTrashAsync(path, ct);
    }

    public Task<int> CleanTrashAsync(TimeSpan maxAge, long maxBytes, CancellationToken ct) => _files.CleanTrashAsync(maxAge, maxBytes, ct);

    public Task MoveAsync(RelativePath source, RelativePath target, CancellationToken ct)
    {
        if (_placeholders.TryRemove(source.Value, out var placeholder))
        {
            _placeholders[target.Value] = placeholder;
        }

        return _files.MoveAsync(source, target, ct);
    }

    public Task CreateAsync(RelativePath path, long size, DateTimeOffset mtime, ContentHash hash, CancellationToken ct)
    {
        var full = Full(path.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, []);
        File.SetLastWriteTimeUtc(full, mtime.UtcDateTime);
        _placeholders[path.Value] = (size, new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero), hash);
        InSync[path.Value] = true;
        return Task.CompletedTask;
    }

    public Task SetInSyncAsync(RelativePath path, bool inSync, CancellationToken ct)
    {
        InSync[path.Value] = inSync;
        return Task.CompletedTask;
    }

    /// <summary>Wie ein Programm, das den Platzhalter öffnet: der Inhalt kommt über den Hydrator, danach ist die Datei vollständig.</summary>
    public async Task<Result<long>> HydrateAsync(string path, Hydrator hydrator)
    {
        var (size, mtime, hash) = _placeholders[path];
        using var content = new MemoryStream();
        var result = await hydrator.CopyToAsync(RelativePath.Parse(path), hash, size, async (data, ct) => await content.WriteAsync(data, ct), default);
        _written[path] = content.Length;
        if (result.IsSuccess)
        {
            var full = Full(path);
            File.WriteAllBytes(full, content.ToArray());
            File.SetLastWriteTimeUtc(full, mtime.UtcDateTime);
            _placeholders.TryRemove(path, out _);
        }

        return result;
    }

    /// <summary>Der Nutzer benennt im Explorer um: Der Platzhalter bleibt einer.</summary>
    public void MoveOnDisk(string from, string to)
    {
        File.Move(Full(from), Full(to));
        if (_placeholders.TryRemove(from, out var placeholder))
        {
            _placeholders[to] = placeholder;
        }
    }

    private LocalEntry Adjust(LocalEntry entry)
    {
        if (entry.Kind != NodeKind.File || !_placeholders.TryGetValue(entry.Path.Value, out var placeholder))
        {
            return entry;
        }

        if (entry.Size != 0 || entry.MTime != placeholder.MTime)
        {
            _placeholders.TryRemove(entry.Path.Value, out _); // beschrieben: jetzt eine gewöhnliche Datei
            return entry;
        }

        return entry with { Size = placeholder.Size, IsCloudOnly = true };
    }

    private string Full(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Hydrator und Statusbeobachter einzeln.</summary>
public sealed class HydratorTests : IDisposable
{
    private readonly Cluster _cluster = new();

    public void Dispose() => _cluster.Dispose();

    [Fact]
    public async Task A_corrupted_block_stops_before_it_is_passed_on()
    {
        var a = _cluster.AddClient("A");
        a.WriteBytes("a.bin", Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7)).ToArray());
        await a.SyncOkAsync();
        var node = (await a.Remote.GetNodeAsync(RelativePath.Parse("a.bin"), default)).Value!;
        var corrupt = new CorruptingRemote(a.Remote, node.Blocks[1]);
        long passed = 0;

        var result = await new Hydrator(corrupt).CopyToAsync(
            RelativePath.Parse("a.bin"), node.State!.Hash, node.State.Size, (data, _) =>
            {
                passed += data.Length;
                return Task.CompletedTask;
            },
            default);

        Assert.Equal("block_corrupt", result.Error?.Code);
        Assert.True(node.Blocks.Count > 2);
        Assert.Equal(await LengthOfAsync(a.Remote, node.Blocks[0]), passed); // nur der erste, geprüfte Block ging weiter
    }

    [Fact]
    public async Task Without_a_known_version_the_size_must_match()
    {
        var a = _cluster.AddClient("A");
        a.Write("a.txt", "twelve chars");
        await a.SyncOkAsync();
        var hydrator = new Hydrator(a.Remote);
        using var sink = new MemoryStream();

        var wrongSize = await hydrator.CopyToAsync(RelativePath.Parse("a.txt"), null, 5, (d, ct) => sink.WriteAsync(d, ct).AsTask(), default);
        var rightSize = await hydrator.CopyToAsync(RelativePath.Parse("a.txt"), null, 12, (d, ct) => sink.WriteAsync(d, ct).AsTask(), default);

        Assert.Equal("placeholder_stale", wrongSize.Error?.Code);
        Assert.Equal(12, rightSize.Value);
    }

    [Fact]
    public void The_status_observer_marks_results_and_ignores_deletions()
    {
        var placeholders = new RecordingPlaceholders();
        var errors = new List<string>();
        var observer = new PlaceholderStatusObserver(placeholders, (path, _) => errors.Add(path.Value));
        var p = RelativePath.Parse("a.txt");

        observer.PathFinished(p, Result.Ok(new PathOutcome(p, OutcomeKind.Uploaded)));
        observer.PathFinished(p, Result.Fail<PathOutcome>(SyncErrorKind.Permanent, "x", "kaputt"));
        observer.PathFinished(p, Result.Ok(new PathOutcome(p, OutcomeKind.DeletedRemote)));
        observer.PathFinished(p, Result.Ok(new PathOutcome(RelativePath.Parse("b.txt"), OutcomeKind.Moved)));
        placeholders.Fail = true;
        observer.PathFinished(p, Result.Ok(new PathOutcome(p, OutcomeKind.Unchanged)));

        Assert.Equal(["a.txt=True", "a.txt=False", "b.txt=True"], placeholders.Calls);
        Assert.Equal(["a.txt"], errors);
    }

    private static async Task<long> LengthOfAsync(InProcessRemoteApi remote, ContentHash block)
    {
        await using var stream = (await remote.DownloadBlockAsync(block, default)).Value!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.Length;
    }

    private sealed class RecordingPlaceholders : IPlaceholders
    {
        public List<string> Calls { get; } = [];

        public bool Fail { get; set; }

        public Task CreateAsync(RelativePath path, long size, DateTimeOffset mtime, ContentHash hash, CancellationToken ct) => Task.CompletedTask;

        public Task SetInSyncAsync(RelativePath path, bool inSync, CancellationToken ct)
        {
            if (Fail)
            {
                throw new IOException("gesperrt");
            }

            Calls.Add($"{path.Value}={inSync}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Liefert einen bestimmten Block verfälscht aus.</summary>
    private sealed class CorruptingRemote(IRemoteApi inner, ContentHash corrupt) : IRemoteApi
    {
        public Task<Result<RemoteNode?>> GetNodeAsync(RelativePath path, CancellationToken ct) => inner.GetNodeAsync(path, ct);

        public Task<Result<IReadOnlyList<SnapshotEntry>>> GetSnapshotAsync(RelativePath root, CancellationToken ct) => inner.GetSnapshotAsync(root, ct);

        public Task<Result<long>> GetLatestRevisionAsync(CancellationToken ct) => inner.GetLatestRevisionAsync(ct);

        public Task<Result<Contracts.ChangesResponse>> GetChangesAsync(long after, int limit, TimeSpan? wait, CancellationToken ct) =>
            inner.GetChangesAsync(after, limit, wait, ct);

        public Task<Result<IReadOnlyList<ContentHash>>> MissingBlocksAsync(IReadOnlyList<ContentHash> hashes, CancellationToken ct) =>
            inner.MissingBlocksAsync(hashes, ct);

        public Task<Result<bool>> UploadBlockAsync(ContentHash hash, Stream content, CancellationToken ct) => inner.UploadBlockAsync(hash, content, ct);

        public async Task<Result<Stream>> DownloadBlockAsync(ContentHash hash, CancellationToken ct)
        {
            var result = await inner.DownloadBlockAsync(hash, ct);
            if (hash != corrupt || !result.IsSuccess)
            {
                return result;
            }

            using var original = result.Value!;
            var bytes = new MemoryStream();
            await original.CopyToAsync(bytes, ct);
            var data = bytes.ToArray();
            data[0] ^= 0xFF;
            return Result.Ok<Stream>(new MemoryStream(data));
        }

        public Task<Result<Contracts.CommitResponse>> CommitAsync(Contracts.CommitRequest request, string? idempotencyKey, CancellationToken ct) =>
            inner.CommitAsync(request, idempotencyKey, ct);
    }
}
