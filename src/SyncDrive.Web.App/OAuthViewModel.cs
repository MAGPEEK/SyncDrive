using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// Organisationseinstellungen, Reiter „Anmeldung (OAuth 2.0)“: Anbindung an einen Identitätsanbieter. Das Client-Secret kommt nie zurück;
/// ein leeres Feld behält das gespeicherte.
/// </summary>
public sealed class OAuthViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public OAuthSettingsInfo? Settings { get; private set; }

    public async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync(async () => Settings = await api.GetOAuthSettingsAsync(ct));
        OnChanged();
    }

    /// <param name="clientSecret">Neues Secret; leer = das gespeicherte behalten.</param>
    public async Task<bool> SaveAsync(OAuthSettingsInfo edited, string clientSecret, CancellationToken ct)
    {
        var request = new OAuthSettingsRequest(
            edited.Enabled, edited.DisplayName, edited.Issuer, edited.AuthorizationEndpoint, edited.TokenEndpoint, edited.UserInfoEndpoint,
            edited.ClientId, clientSecret.Length > 0 ? clientSecret : null, edited.Scopes, edited.UserNameClaim, edited.CreateUsers);
        OAuthSettingsInfo? saved = null;
        var ok = await RunAsync(async () => saved = await api.SaveOAuthSettingsAsync(request, ct));
        if (ok)
        {
            Settings = saved;
            Inform(Texts.Saved);
        }

        OnChanged();
        return ok;
    }
}
