using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// Anmeldung im Browser mit Name und Passwort. Dazu die Anmeldeseite des Servers: selbst gewählter Name, Text, Hintergrundbild.
/// Prüft beim Start das Sitzungs-Cookie.
/// </summary>
public sealed class SessionViewModel(IDriveApi api, WebTexts texts)
{
    public event Action? Changed;

    public WebTexts Texts { get; } = texts;

    public WebSessionInfo? Session { get; private set; }

    public PortalInfo Portal { get; private set; } = new("SyncDrive", null, null);

    /// <summary>Beim Start, bis feststeht, ob schon eine Sitzung besteht (dann kein Anmeldeformular aufblitzen lassen).</summary>
    public bool IsChecking { get; private set; } = true;

    public bool IsBusy { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Anmeldung über den Identitätsanbieter der Organisation; aus, solange unbekannt.</summary>
    public OAuthLoginInfo OAuth { get; private set; } = new(false, string.Empty);

    /// <summary>Der Knopf „Anmelden mit …“ führt hierhin; der Server leitet zum Anbieter weiter.</summary>
    public const string OAuthStartUrl = "api/v1/web/oauth/start";

    /// <summary>Rückkehr vom Anbieter ohne Erfolg (<c>?signin_error=</c>): nur bekannte Codes werden zu Text.</summary>
    public void ShowRefusal(string? code)
    {
        Error = code is null ? null : Texts.OAuthRefusalText(code);
        Changed?.Invoke();
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            Portal = await api.GetPortalAsync(ct);
            Session = await api.GetSessionAsync(ct);
            if (Session is null)
            {
                OAuth = await api.GetOAuthLoginAsync(ct); // nur die Anmeldeseite braucht den Knopf
            }
        }
        catch (Exception e) when (e is DriveException or HttpRequestException)
        {
            // Server nicht erreichbar oder Anmeldeseite fehlt: mit den Vorgaben anmelden lassen
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke();
        }
    }

    public async Task<bool> SignInAsync(string userName, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            Error = Texts.SignInFailed;
            Changed?.Invoke();
            return false;
        }

        IsBusy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            Session = await api.SignInAsync(userName.Trim(), password, ct);
            return true;
        }
        catch (DriveException e)
        {
            Error = e.Code == ErrorCode.Unauthorized ? Texts.SignInFailed : Texts.For(e);
            return false;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        try
        {
            await api.SignOutAsync(ct);
        }
        catch (DriveException)
        {
            // schon abgelaufen: lokal trotzdem abmelden
        }

        Session = null;
        Changed?.Invoke();
    }

    /// <summary>Der Server hat eine Anfrage abgewiesen (Sitzung widerrufen oder abgelaufen): zurück zur Anmeldung.</summary>
    public void Expired()
    {
        Session = null;
        Error = Texts.NotSignedIn;
        Changed?.Invoke();
    }

    /// <summary>Nach dem Ändern der Anmeldeseite im Admin-Dialog.</summary>
    public void PortalChanged(PortalInfo portal)
    {
        Portal = portal;
        Changed?.Invoke();
    }
}
