using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using static SyncDrive.Infra.CloudFiles.NativeMethods;

namespace SyncDrive.Infra.CloudFiles;

/// <summary>
/// Verbindung eines Sync-Roots mit diesem Prozess. Windows ruft <c>FETCH_DATA</c>, wenn ein Programm einen Platzhalter liest; der Inhalt kommt
/// über den <see cref="Hydrator"/> vom Server und geht in Stücken zu 1 MiB zurück (Vielfache von 4 KiB, wie die API es verlangt; nur das letzte
/// Stück endet am Dateiende). Der eigene Prozess holt nie unbemerkt Inhalt (<c>BLOCK_SELF_IMPLICIT_HYDRATION</c>): Der Sync liest Platzhalter nicht.
/// </summary>
internal sealed class CloudConnection : IAsyncDisposable
{
    private const int ChunkSize = 1024 * 1024;

    private readonly string _root;
    private readonly Hydrator _hydrator;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _transfers = new();
    private readonly ConcurrentDictionary<Task, bool> _running = new();
    private GCHandle _self;
    private IntPtr _table;
    private long _key;

    private CloudConnection(string root, Hydrator hydrator, Action<string>? log)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _hydrator = hydrator;
        _log = log;
    }

    public static unsafe CloudConnection Connect(string root, Hydrator hydrator, Action<string>? log)
    {
        var connection = new CloudConnection(root, hydrator, log);
        connection._self = GCHandle.Alloc(connection);
        var table = (CallbackRegistration*)NativeMemory.Alloc((nuint)(3 * sizeof(CallbackRegistration)));
        connection._table = (IntPtr)table;
        table[0] = new CallbackRegistration
        {
            Type = CallbackFetchData,
            Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CallbackInfo*, FetchDataParameters*, void>)&OnFetchData,
        };
        table[1] = new CallbackRegistration
        {
            Type = CallbackCancelFetchData,
            Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CallbackInfo*, IntPtr, void>)&OnCancelFetchData,
        };
        table[2] = new CallbackRegistration { Type = CallbackNone };
        var hr = CfConnectSyncRoot(
            connection._root, table, GCHandle.ToIntPtr(connection._self), ConnectBlockSelfImplicitHydration | ConnectRequireFullFilePath, out connection._key);
        if (hr < 0)
        {
            connection.Free();
            Check(hr, $"Sync-Root {root} verbinden");
        }

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_table == IntPtr.Zero)
        {
            return;
        }

        Check(CfDisconnectSyncRoot(_key), "Sync-Root trennen");
        foreach (var transfer in _transfers.Values)
        {
            await transfer.CancelAsync();
        }

        await Task.WhenAll(_running.Keys);
        Free();
    }

    private unsafe void Free()
    {
        NativeMemory.Free((void*)_table);
        _table = IntPtr.Zero;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchData(CallbackInfo* info, FetchDataParameters* parameters)
    {
        var connection = (CloudConnection)GCHandle.FromIntPtr(info->CallbackContext).Target!;
        var request = new FetchRequest(
            info->ConnectionKey, info->TransferKey, info->RequestKey,
            FullPath(Marshal.PtrToStringUni(info->VolumeDosName), Marshal.PtrToStringUni(info->NormalizedPath)),
            info->FileIdentityLength == 0 ? null : Encoding.UTF8.GetString((byte*)info->FileIdentity, (int)info->FileIdentityLength),
            info->FileSize, parameters->RequiredFileOffset, parameters->RequiredLength);
        connection.Start(request); // Antwort kommt später über CfExecute; der Rückruf muss sofort zurückkehren
    }

    /// <summary>Der Pfad kommt ab der Wurzel des Laufwerks (<c>\Users\…</c>); enthält er schon ein Laufwerk, gilt er so.</summary>
    private static string FullPath(string? volume, string? normalized) =>
        normalized is null ? string.Empty
        : normalized.Contains(':', StringComparison.Ordinal) || normalized.StartsWith(@"\\", StringComparison.Ordinal) ? normalized
        : volume + normalized;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnCancelFetchData(CallbackInfo* info, IntPtr parameters)
    {
        var connection = (CloudConnection)GCHandle.FromIntPtr(info->CallbackContext).Target!;
        if (connection._transfers.TryGetValue(info->TransferKey, out var transfer))
        {
            transfer.Cancel();
        }
    }

    private void Start(FetchRequest request)
    {
        var cancel = new CancellationTokenSource();
        _transfers[request.TransferKey] = cancel;
        var task = Task.Run(() => FetchAsync(request, cancel.Token));
        _running[task] = true;
        task.ContinueWith(
            t =>
            {
                _running.TryRemove(t, out _);
                _transfers.TryRemove(request.TransferKey, out _);
                cancel.Dispose();
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task FetchAsync(FetchRequest request, CancellationToken ct)
    {
        try
        {
            if (ToRelative(request.FullPath) is not { } path)
            {
                Fail(request, "Pfad liegt nicht im Sync-Ordner");
                return;
            }

            var buffer = new byte[ChunkSize];
            var filled = 0;
            long offset = 0;
            var result = await _hydrator.CopyToAsync(
                path,
                request.Identity is { } identity && ContentHash.TryParse(identity, out var hash) ? hash : (ContentHash?)null,
                request.FileSize,
                (data, _) =>
                {
                    var rest = data.Span;
                    while (!rest.IsEmpty)
                    {
                        var take = Math.Min(rest.Length, ChunkSize - filled);
                        rest[..take].CopyTo(buffer.AsSpan(filled));
                        filled += take;
                        rest = rest[take..];
                        if (filled == ChunkSize)
                        {
                            Transfer(request, buffer, offset, filled);
                            offset += filled;
                            filled = 0;
                        }
                    }

                    return Task.CompletedTask;
                },
                ct);
            if (!result.IsSuccess)
            {
                Fail(request, result.Error!.Message);
                return;
            }

            if (filled > 0)
            {
                Transfer(request, buffer, offset, filled); // letztes Stück, endet am Dateiende
            }
        }
        catch (Exception e) // jede Anfrage braucht eine Antwort, sonst wartet das öffnende Programm bis zur Zeitüberschreitung
        {
            Fail(request, e.Message);
        }
    }

    private static unsafe void Transfer(FetchRequest request, byte[] data, long offset, int length)
    {
        fixed (byte* pointer = data)
        {
            var parameters = new TransferDataParameters
            {
                ParamSize = (uint)sizeof(TransferDataParameters),
                Buffer = (IntPtr)pointer,
                Offset = offset,
                Length = length,
            };
            Check(CfExecute(Operation(request), ref parameters), "Inhalt übergeben");
        }
    }

    private unsafe void Fail(FetchRequest request, string reason)
    {
        _log?.Invoke($"Abruf von {request.FullPath} gescheitert: {reason}");
        var parameters = new TransferDataParameters
        {
            ParamSize = (uint)sizeof(TransferDataParameters),
            CompletionStatus = StatusUnsuccessful,
            Offset = request.RequiredOffset,
            Length = request.RequiredLength,
        };
        _ = CfExecute(Operation(request), ref parameters); // scheitert auch das, hat Windows die Anfrage schon verworfen
    }

    private static unsafe OperationInfo Operation(FetchRequest request) => new()
    {
        StructSize = (uint)sizeof(OperationInfo),
        Type = OperationTransferData,
        ConnectionKey = request.ConnectionKey,
        TransferKey = request.TransferKey,
        RequestKey = request.RequestKey,
    };

    private RelativePath? ToRelative(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            || !RelativePath.TryParse(relative.Replace('\\', '/'), out var path)
            ? null
            : path;
    }

    private sealed record FetchRequest(
        long ConnectionKey, long TransferKey, long RequestKey, string FullPath, string? Identity, long FileSize, long RequiredOffset, long RequiredLength);
}
