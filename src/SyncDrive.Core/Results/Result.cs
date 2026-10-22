namespace SyncDrive.Core.Results;

/// <summary>Wie ein Fehler zu behandeln ist.</summary>
public enum SyncErrorKind
{
    /// <summary>Netz weg, Server überlastet, Datei gesperrt: später mit Backoff erneut versuchen.</summary>
    Transient,

    /// <summary>Wird durch Wiederholen nicht besser: Eintrag im Verlauf, Pfad bleibt unsynchronisiert.</summary>
    Permanent,

    /// <summary>Die Sitzung kann nicht weiterarbeiten (z. B. Anmeldung entzogen): pausieren und melden.</summary>
    Fatal,
}

/// <param name="Code">Stabiler Code (bei Server-Fehlern der <c>ErrorCode</c> des Protokolls), nie Freitext für Logik.</param>
public sealed record SyncError(SyncErrorKind Kind, string Code, string Message);

/// <summary>Erwartbares Ergebnis statt Exception: entweder ein Wert oder ein <see cref="SyncError"/>.</summary>
public readonly record struct Result<T>(T? Value, SyncError? Error)
{
    public bool IsSuccess => Error is null;
}

public static class Result
{
    public static Result<T> Ok<T>(T value) => new(value, null);

    public static Result<T> Fail<T>(SyncError error) => new(default, error);

    public static Result<T> Fail<T>(SyncErrorKind kind, string code, string message) =>
        new(default, new SyncError(kind, code, message));
}
