using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Chunking;
using SyncDrive.Core.Filtering;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Fs;
using SyncDrive.Server.App.Spaces;
using SyncDrive.Testing;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>Ein Server (echte Logik, Speicher im Arbeitsspeicher) und beliebig viele Clients mit echten Ordnern im Temp-Verzeichnis.</summary>
public sealed class Cluster : IDisposable
{
    public const string Space = "s1";

    /// <summary>Kleine Blöcke, damit Delta und Deduplizierung schon bei kleinen Dateien sichtbar sind.</summary>
    public static readonly ChunkerOptions SmallChunks = new(MinSize: 256, AverageSize: 1024, MaxSize: 4096);

    private readonly List<TestClient> _clients = [];

    public Cluster()
    {
        Store.AddSpace(Space);
        Service = new SpaceService(Store, Blocks, Notifier);
    }

    /// <summary>Weckt Long-Polls, wie im echten Server-Prozess.</summary>
    public InProcessChangeNotifier Notifier { get; } = new();

    public InMemorySpaceStore Store { get; } = new();

    public InMemoryBlockStore Blocks { get; } = new();

    public SpaceService Service { get; }

    public TestClient AddClient(
        string device,
        ClientSettings? settings = null,
        SyncDirection direction = SyncDirection.TwoWay,
        IReadOnlyList<IFilterRule>? rules = null,
        Func<SyncTaskConfig, SyncTaskConfig>? configure = null,
        RelativePath? remoteRoot = null)
    {
        var client = new TestClient(this, device, settings ?? new ClientSettings(), direction, rules ?? [], configure, remoteRoot);
        _clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        Store.Dispose();
    }
}

public sealed class TestClient : IDisposable
{
    public TestClient(
        Cluster cluster, string device, ClientSettings settings, SyncDirection direction, IReadOnlyList<IFilterRule> rules,
        Func<SyncTaskConfig, SyncTaskConfig>? configure = null, RelativePath? remoteRoot = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"syncdrive-scn-{device}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Local = new LocalFileStore(Root);
        Remote = new InProcessRemoteApi(cluster.Service, Cluster.Space);
        var config = new SyncTaskConfig(device, direction, settings, new FilterEngine(rules), Cluster.SmallChunks);
        config = configure?.Invoke(config) ?? config;
        var remote = SubtreeRemoteApi.For(Remote, remoteRoot ?? RelativePath.Root);
        Engine = new SyncEngine(Local, remote, State, config, Log, TimeProvider.System);
        Tracker = new TaskStatusTracker(device);
        Session = new SyncSession(Engine, Local, remote, State, Tracker);
    }

    public string Root { get; }

    public LocalFileStore Local { get; }

    public InProcessRemoteApi Remote { get; }

    public InMemorySessionState State { get; } = new();

    public RecordingActivityLog Log { get; } = new();

    public SyncEngine Engine { get; }

    public SyncSession Session { get; }

    public TaskStatusTracker Tracker { get; }

    public async Task<RescanReport> SyncAsync(bool startup = false)
    {
        var report = await Session.RescanAsync(RelativePath.Root, startup, CancellationToken.None);
        return report;
    }

    /// <summary>Synchronisiert und verlangt, dass nichts fehlschlägt.</summary>
    public async Task<RescanReport> SyncOkAsync(bool startup = false)
    {
        var report = await SyncAsync(startup);
        Assert.True(report.Failures.Count == 0, string.Join("; ", report.Failures.Select(f => $"{f.Path}: {f.Error.Code} {f.Error.Message}")));
        return report;
    }

    /// <summary>Erstabgleich mit Cursor wie beim Programmstart.</summary>
    public async Task StartOkAsync(bool startup = true)
    {
        var report = await Session.StartAsync(startup, CancellationToken.None);
        Assert.True(report.Failures.Count == 0, string.Join("; ", report.Failures.Select(f => $"{f.Path}: {f.Error.Code}")));
    }

    public Task<SyncDrive.Core.Results.Result<int>> PullAsync(TimeSpan? wait = null, CancellationToken ct = default) =>
        Session.PullChangesAsync(wait, ct);

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content, DateTime? mtimeUtc = null) => WriteBytes(relative, System.Text.Encoding.UTF8.GetBytes(content), mtimeUtc);

    public void WriteBytes(string relative, byte[] content, DateTime? mtimeUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        File.WriteAllBytes(Full(relative), content);
        if (mtimeUtc is { } t)
        {
            File.SetLastWriteTimeUtc(Full(relative), t);
        }
    }

    public string Read(string relative) => File.ReadAllText(Full(relative));

    public bool Exists(string relative) => File.Exists(Full(relative)) || Directory.Exists(Full(relative));

    public void Delete(string relative)
    {
        if (Directory.Exists(Full(relative)))
        {
            Directory.Delete(Full(relative), recursive: true);
        }
        else
        {
            File.Delete(Full(relative));
        }
    }

    /// <summary>
    /// Alle Dateien im Sync-Ordner (ohne den internen Ordner) als relativer Pfad → Inhalt. Läuft der Client gerade, kann sich der Ordner beim
    /// Lesen ändern; dann wird neu gelesen.
    /// </summary>
    public Dictionary<string, string> Files()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Directory
                    .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(Root, f).Replace(Path.DirectorySeparatorChar, '/'))
                    .Where(p => !p.StartsWith(LocalFileStore.InternalFolder + "/", StringComparison.Ordinal))
                    .ToDictionary(p => p, Read);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    public IEnumerable<string> TrashFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root, LocalFileStore.InternalFolder, "trash"), "*", SearchOption.AllDirectories).Select(File.ReadAllText);

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
