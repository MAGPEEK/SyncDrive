using System.Net;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;

namespace SyncDrive.Infra.Http;

/// <summary>Setzt die Proxy-Einstellung („Proxy“-Reiter) auf einen Handler: kein Proxy, Systemeinstellung oder manuell mit Anmeldung.</summary>
public static class Proxies
{
    public static void Apply(SocketsHttpHandler handler, ProxyConfig? proxy)
    {
        switch (proxy?.Mode)
        {
            case ProxyMode.Manual when proxy.Host is { Length: > 0 } host:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(host, proxy.Port ?? 8080)
                {
                    Credentials = proxy.UserName is { Length: > 0 } user ? new NetworkCredential(user, proxy.Password) : null,
                };
                break;
            case ProxyMode.Auto:
                handler.UseProxy = true; // Systemeinstellung (WinINET/Umgebungsvariablen)
                break;
            default:
                handler.UseProxy = false;
                break;
        }
    }
}
