using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Paths;

namespace SyncDrive.Infra.Ipc;

/// <summary>
/// Wire-Format zwischen Daemon und Oberfläche: eine JSON-Nachricht je Zeile (UTF-8) über eine Named Pipe, die nur der eigene Benutzer
/// öffnen darf. Anfragen tragen die Protokollversion <c>v</c>, eine Id und eine Methode; Antworten dieselbe Id; Ereignisse (<c>event</c>)
/// schickt der Daemon unaufgefordert, nachdem die Oberfläche sie mit <c>status.watch</c> abonniert hat.
/// </summary>
/// <example>
/// → {"v":1,"id":3,"method":"task.pause","params":{"taskId":"t1"}}
/// ← {"id":3,"ok":true}
/// ← {"event":"status","data":{"tray":"Paused",…}}
/// </example>
public static class IpcProtocol
{
    /// <summary>Wird bei inkompatiblen Änderungen erhöht; der Daemon lehnt andere Versionen mit <c>unsupported_version</c> ab.</summary>
    public const int Version = 1;

    internal const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new RelativePathConverter() },
    };

    /// <summary>Pipe-Name je Benutzer; der Name selbst verrät den Benutzernamen nicht.</summary>
    public static string DefaultPipeName(string userName) =>
        "syncdrive-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userName.ToUpperInvariant())))[..16];

    internal static class Methods
    {
        public const string Status = "status.get";
        public const string Watch = "status.watch";
        public const string Activity = "activity.recent";
        public const string Pause = "task.pause";
        public const string Resume = "task.resume";
        public const string Tasks = "tasks.list";
        public const string Connections = "connections.list";
        public const string SignIn = "auth.signin";
        public const string Browse = "server.browse";
        public const string CheckFolder = "folder.check";
        public const string CreateTask = "task.create";
        public const string DeleteTask = "task.delete";
        public const string GetSettings = "settings.get";
        public const string SaveSettings = "settings.save";
        public const string GetTask = "task.get";
        public const string UpdateTask = "task.update";
        public const string UpdateConnection = "connection.update";
        public const string Usage = "usage.list";
        public const string ProxyPassword = "proxy.password";
        public const string Notifications = "notifications.list";
        public const string NotificationsRead = "notifications.read";
        public const string PathInfo = "shell.info";
        public const string Versions = "shell.versions";
        public const string SaveVersion = "shell.saveversion";
        public const string RestoreVersion = "shell.restoreversion";
        public const string FolderSync = "shell.foldersync";
    }

    internal static class Errors
    {
        public const string BadRequest = "bad_request";
        public const string UnsupportedVersion = "unsupported_version";
        public const string UnknownMethod = "unknown_method";
        public const string UnknownTask = "unknown_task";
        public const string Internal = "internal";
        public const string Disconnected = "disconnected";
    }

    internal const string StatusEvent = "status";

    private sealed class RelativePathConverter : JsonConverter<RelativePath>
    {
        public override RelativePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            RelativePath.Parse(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, RelativePath value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

internal sealed record IpcRequest(int V, long Id, string Method, JsonElement? Params);

internal sealed record IpcError(string Code, string Message);

/// <summary>Antwort (<see cref="Id"/> gesetzt) oder Ereignis (<see cref="Event"/> gesetzt).</summary>
internal sealed record IpcMessage(
    long? Id = null,
    bool? Ok = null,
    JsonElement? Result = null,
    IpcError? Error = null,
    string? Event = null,
    JsonElement? Data = null);

internal sealed record TaskParams(string? TaskId);

internal sealed record LimitParams(int Limit);

internal sealed record BrowseParams(string ConnectionId, string? SpaceId, string Path);

internal sealed record CheckFolderParams(string LocalPath, bool OnDemand);

internal sealed record UpdateConnectionParams(string ConnectionId, SignInRequest Request);

internal sealed record PasswordParams(string? Password);

internal sealed record PathParams(string Path);

internal sealed record VersionParams(string Path, long Revision, string? Target = null);

internal sealed record FolderSyncParams(string Path, bool Synced);

/// <summary>Der Daemon hat eine Anfrage abgelehnt oder ist nicht erreichbar.</summary>
public sealed class IpcException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}
