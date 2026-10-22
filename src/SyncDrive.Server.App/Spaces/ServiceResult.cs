using SyncDrive.Contracts;

namespace SyncDrive.Server.App.Spaces;

/// <summary>Erwartbarer Fehler eines Use-Cases; die Host-Schicht bildet ihn auf einen HTTP-Status ab.</summary>
public sealed record ServiceError(
    string Code,
    string Message,
    NodeInfo? Current = null,
    IReadOnlyList<string>? MissingBlocks = null);

public readonly record struct ServiceResult<T>(T? Value, ServiceError? Error)
{
    public bool IsSuccess => Error is null;
}

public static class ServiceResult
{
    public static ServiceResult<T> Ok<T>(T value) => new(value, null);

    public static ServiceResult<T> Fail<T>(ServiceError error) => new(default, error);
}
