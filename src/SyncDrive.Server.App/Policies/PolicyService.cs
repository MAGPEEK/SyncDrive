using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Policies;

/// <summary>Ablage der Richtlinien als Daten (Richtlinien sind Daten, keine Code-Verzweigungen).</summary>
public interface IPolicyStore
{
    /// <summary>Null, solange nie gespeichert wurde (dann gelten die Vorgaben).</summary>
    Task<SharingPolicies?> LoadAsync(CancellationToken ct);

    Task SaveAsync(SharingPolicies policies, CancellationToken ct);
}

/// <summary>
/// Freigabe-Richtlinien (M7, 0.16.0): öffentliche Links und Dateianfragen an oder aus, Passwortpflicht, Höchstdauer. Gelesen wird oft (jede
/// Link-Auflösung), geschrieben selten; deshalb hält der Dienst den Stand im Speicher und liest nach dem Speichern neu.
/// </summary>
public sealed class PolicyService(IPolicyStore store)
{
    public const int MaxDays = 3650;

    private SharingPolicies? _current;

    public async Task<SharingPolicies> GetAsync(CancellationToken ct) =>
        _current ??= await store.LoadAsync(ct) ?? new SharingPolicies();

    public async Task<ServiceResult<SharingPolicies>> UpdateAsync(Principal caller, SharingPolicies policies, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Fail<SharingPolicies>(new ServiceError(ErrorCode.Forbidden, "Nur für Administratoren."));
        }

        if (policies.LinkMaxDays is < 1 or > MaxDays || policies.RequestMaxDays is < 1 or > MaxDays)
        {
            return ServiceResult.Fail<SharingPolicies>(new ServiceError(ErrorCode.InvalidRequest, $"Höchstdauer zwischen 1 und {MaxDays} Tagen."));
        }

        await store.SaveAsync(policies, ct);
        _current = policies;
        return ServiceResult.Ok(policies);
    }

    /// <summary>
    /// Prüft einen Link gegen die Richtlinien: Art erlaubt, Passwort vorhanden, Ablaufdatum innerhalb der Höchstdauer. Null, wenn er passt.
    /// </summary>
    public async Task<ServiceError?> CheckLinkAsync(string kind, bool hasPassword, DateTimeOffset? expiresAt, DateTimeOffset now, CancellationToken ct)
    {
        var p = await GetAsync(ct);
        var (enabled, passwordRequired, maxDays) = kind == LinkKinds.Request
            ? (p.FileRequests, p.RequestPasswordRequired, p.RequestMaxDays)
            : (p.PublicLinks, p.LinkPasswordRequired, p.LinkMaxDays);
        if (!enabled)
        {
            return new ServiceError(ErrorCode.Forbidden, kind == LinkKinds.Request ? "Dateianfragen sind abgeschaltet." : "Öffentliche Links sind abgeschaltet.");
        }

        if (passwordRequired && !hasPassword)
        {
            return new ServiceError(ErrorCode.InvalidRequest, "Für diesen Link ist ein Passwort vorgeschrieben.");
        }

        // Kalendertage bis Tagesende: ein Tag Spielraum, damit das letzte erlaubte Datum nicht von Uhrzeit und Zeitzone abhängt
        if (maxDays is { } days && (expiresAt is not { } expires || expires > now.AddDays(days + 1)))
        {
            return new ServiceError(ErrorCode.InvalidRequest, $"Der Link braucht ein Ablaufdatum innerhalb von {days} Tagen.");
        }

        return null;
    }

    /// <summary>Darf ein bestehender Link dieser Art noch aufgelöst werden? (Abschalten wirkt sofort auch auf verschickte Links.)</summary>
    public async Task<bool> KindEnabledAsync(string kind, CancellationToken ct)
    {
        var p = await GetAsync(ct);
        return kind == LinkKinds.Request ? p.FileRequests : p.PublicLinks;
    }
}
