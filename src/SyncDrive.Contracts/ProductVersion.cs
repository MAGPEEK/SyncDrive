using System.Reflection;

namespace SyncDrive.Contracts;

/// <summary>Produktversion aus <c>Directory.Build.props</c> (Semantic Versioning), gleich für Server, Clients und Weboberfläche.</summary>
public static class ProductVersion
{
    /// <summary>„0.12.0“, ohne den Commit-Anhang, den das SDK an die Informationsversion hängt.</summary>
    public static string Current { get; } =
        (typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}

/// <summary>Antwort von <c>GET /api/v1/info</c> (ohne Anmeldung): welche Version der Server hat.</summary>
public sealed record ServerInfo(string Product, string Version);
