namespace SyncDrive.Contracts;

/// <summary>Stabile Fehlercodes des Protokolls (<c>problem+json</c>, Feld <c>code</c>). Nie Freitext als Logik.</summary>
public static class ErrorCode
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidPath = "invalid_path";
    public const string SpaceNotFound = "space_not_found";
    public const string NotFound = "not_found";
    public const string RevisionConflict = "revision_conflict";
    public const string TypeMismatch = "type_mismatch";
    public const string ParentMissing = "parent_missing";
    public const string AlreadyExists = "already_exists";
    public const string BlocksMissing = "blocks_missing";
    public const string BlockHashMismatch = "block_hash_mismatch";
    public const string CursorExpired = "cursor_expired";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string Locked = "locked";

    /// <summary>Öffentlicher Link abgelaufen (M6b).</summary>
    public const string LinkExpired = "link_expired";

    /// <summary>Öffentlicher Link mit Passwort: erst <c>POST /public/{token}/password</c>.</summary>
    public const string PasswordRequired = "password_required";

    /// <summary>Speicherkontingent des Space-Besitzers ausgeschöpft (M7); HTTP 507.</summary>
    public const string QuotaExceeded = "quota_exceeded";

    /// <summary>Zu viele Fehlversuche mit Passwörtern (M7, Härtung); HTTP 429 mit <c>Retry-After</c>.</summary>
    public const string TooManyAttempts = "too_many_attempts";
}
