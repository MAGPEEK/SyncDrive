using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

public enum SignInStep
{
    UserName,
    Password,
}

/// <summary>
/// Anmeldung im Browser in zwei Schritten (erst der Name, dann das Passwort, jeweils mit dem Pfeil weiter). Dazu die Anmeldeseite
/// des Servers: selbst gewählter Name, Text, Hintergrundbild. Prüft beim Start das Sitzungs-Cookie.
/// </summary>
public sealed class SessionViewModel(IDriveApi api, WebTexts texts)
{
    public event Action? Changed;

    public WebTexts Texts { get; } = texts;

    public WebSessionInfo? Session { get; private set; }

    public PortalInfo Portal { get; private set; } = new("SyncDrive", null, null);

    public SignInStep Step { get; private set; } = SignInStep.UserName;

    /// <summary>Im zweiten Schritt angezeigt („anmelden als …“).</summary>
    public string UserName { get; private set; } = string.Empty;

    /// <summary>Beim Start, bis feststeht, ob schon eine Sitzung besteht (dann kein Anmeldeformular aufblitzen lassen).</summary>
    public bool IsChecking { get; private set; } = true;

    public bool IsBusy { get; private set; }

    public string? Error { get; private set; }

    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            Portal = await api.GetPortalAsync(ct);
            Session = await api.GetSessionAsync(ct);
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

    /// <summary>Erster Schritt: der Name; weiter zum Passwort.</summary>
    public bool Continue(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return false;
        }

        UserName = userName.Trim();
        Step = SignInStep.Password;
        Error = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Zurück zum Namen (anderes Konto).</summary>
    public void BackToUserName()
    {
        Step = SignInStep.UserName;
        Error = null;
        Changed?.Invoke();
    }

    /// <summary>Zweiter Schritt: mit dem gemerkten Namen und dem Passwort anmelden.</summary>
    public Task<bool> SignInAsync(string password, CancellationToken ct) => SignInAsync(UserName, password, ct);

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
            Step = SignInStep.UserName;
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
