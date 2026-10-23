using QRCoder;

namespace SyncDrive.Web.App;

/// <summary>
/// Profilmenü → „Desktop- und Mobil-Apps“: Übersicht mit zwei Wegen. Desktop führt zum Download des Windows-Clients (Adresse vom Server,
/// <c>DesktopClientUrl</c>); Mobil zeigt einen QR-Code mit der Adresse dieser Weboberfläche, die auf dem Telefon im Browser läuft.
/// </summary>
public sealed class AppsViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public enum Page
    {
        Overview,
        Desktop,
        Mobile,
    }

    public Page Current { get; private set; }

    public string? DesktopUrl { get; private set; }

    /// <summary>Adresse der Weboberfläche, wie der Browser sie gerade aufruft.</summary>
    public string WebAddress { get; private set; } = string.Empty;

    /// <summary>QR-Code der <see cref="WebAddress"/> als PNG-Daten-URL; null, bis „Mobil“ gewählt wurde.</summary>
    public string? QrCode { get; private set; }

    public async Task OpenAsync(string webAddress, CancellationToken ct)
    {
        (Current, WebAddress, QrCode) = (Page.Overview, webAddress, null);
        OnChanged();
        if (DesktopUrl is null)
        {
            await RunAsync(async () => DesktopUrl = (await api.GetClientAppsAsync(ct)).DesktopUrl);
            OnChanged();
        }
    }

    public void ShowDesktop() => Show(Page.Desktop);

    public void ShowMobile()
    {
        QrCode ??= QrFor(WebAddress);
        Show(Page.Mobile);
    }

    public void Back() => Show(Page.Overview);

    private void Show(Page page)
    {
        Current = page;
        OnChanged();
    }

    private static string QrFor(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));
    }
}
