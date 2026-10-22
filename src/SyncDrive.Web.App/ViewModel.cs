using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// Gemeinsames aller Ansichten: Änderungsmeldung an die Oberfläche, eine Meldungszeile und die Übersetzung von Serverfehlern. Eine abgelaufene
/// Sitzung wird zu <see cref="SignedOut"/>; nichts wirft bis in die Oberfläche.
/// </summary>
public abstract class ViewModel(WebTexts texts)
{
    public event Action? Changed;

    /// <summary>Die Sitzung ist abgelaufen oder widerrufen; die Oberfläche kehrt zur Anmeldung zurück.</summary>
    public event Action? SignedOut;

    public WebTexts Texts { get; } = texts;

    /// <summary>Letzte Meldung (Fehler oder Hinweis), bis sie geschlossen wird.</summary>
    public string? Message { get; private set; }

    public void DismissMessage()
    {
        Message = null;
        OnChanged();
    }

    protected void OnChanged() => Changed?.Invoke();

    protected void OnSignedOut() => SignedOut?.Invoke();

    protected bool Fail(string message)
    {
        Message = message;
        OnChanged();
        return false;
    }

    protected void Inform(string message)
    {
        Message = message;
        OnChanged();
    }

    /// <summary>Führt eine Serveraktion aus; Fehler werden zur Meldung, eine abgelaufene Sitzung zum Abmelden.</summary>
    /// <param name="explain">Eigene Meldung für bestimmte Fehler dieser Aktion; null nimmt die allgemeine.</param>
    protected async Task<bool> RunAsync(Func<Task> action, Func<DriveException, string?>? explain = null)
    {
        try
        {
            await action();
            return true;
        }
        catch (DriveException e) when (e.Code == ErrorCode.Unauthorized)
        {
            OnSignedOut();
            return false;
        }
        catch (DriveException e)
        {
            return Fail(explain?.Invoke(e) ?? Texts.For(e));
        }
        catch (HttpRequestException e)
        {
            return Fail(string.Format(CultureInfo.CurrentCulture, Texts.GenericError, e.Message));
        }
    }

    /// <summary>Darf in diesem Space geschrieben werden (Editor oder mehr)?</summary>
    public static bool CanWriteIn(SpaceSummary? space) => space?.Role is "editor" or "manager" or "owner";
}
