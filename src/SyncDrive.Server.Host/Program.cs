using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using SyncDrive.Contracts;
using SyncDrive.Infra.Blob;
using SyncDrive.Infra.Imaging;
using SyncDrive.Infra.Security;
using SyncDrive.Infra.Sqlite;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Items;
using SyncDrive.Server.App.Policies;
using SyncDrive.Server.App.Portal;
using SyncDrive.Server.App.Ports;
using SyncDrive.Server.App.Previews;
using SyncDrive.Server.App.Sharing;
using SyncDrive.Server.App.Spaces;
using SyncDrive.Server.Host;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("SYNCDRIVE_");

var services = builder.Services;
services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ServerOptions>>((forwarded, server) =>
{
    if (!server.Value.TrustForwardedHeaders)
    {
        return;
    }

    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.KnownNetworks.Clear(); // Container-Netz: der Proxy hat keine feste Adresse
    forwarded.KnownProxies.Clear();
});

static string DatabasePath(IServiceProvider sp)
{
    var directory = sp.GetRequiredService<IOptions<ServerOptions>>().Value.DataDirectory;
    Directory.CreateDirectory(directory);
    return Path.Combine(directory, "server.db");
}

services.AddSingleton(sp => new SqliteSpaceStore(DatabasePath(sp)));
services.AddSingleton<ISpaceStore>(sp => sp.GetRequiredService<SqliteSpaceStore>());
services.AddSingleton<IAccountStore>(sp => new SqliteAccountStore(DatabasePath(sp)));
services.AddSingleton<IBlockStore>(sp =>
    new FileBlockStore(Path.Combine(sp.GetRequiredService<IOptions<ServerOptions>>().Value.DataDirectory, "blocks")));
services.AddSingleton<IChangeNotifier, InProcessChangeNotifier>();
services.AddSingleton<IPasswordHasher>(sp =>
{
    var o = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    return new Argon2PasswordHasher(new Argon2Options(o.Argon2MemoryKiB, o.Argon2Iterations, o.Argon2Parallelism));
});
services.AddSingleton(TimeProvider.System);
services.AddSingleton<ISpaceQuotas, AccountQuotas>();
services.AddSingleton<SpaceService>();
services.AddSingleton(sp => new AttemptLimiter(sp.GetRequiredService<TimeProvider>(), AttemptLimiter.DefaultWindow));
services.AddSingleton<AccountService>();
services.AddSingleton<IGroupStore>(sp => new SqliteGroupStore(DatabasePath(sp)));
services.AddSingleton<AdminService>();
services.AddSingleton<IUserItemStore>(sp => new SqliteUserItemStore(DatabasePath(sp)));
services.AddSingleton<INotificationStore>(sp => new SqliteNotificationStore(DatabasePath(sp)));
services.AddSingleton<IShareStore>(sp => new SqliteShareStore(DatabasePath(sp)));
services.AddSingleton<IPolicyStore>(sp => new SqlitePolicyStore(DatabasePath(sp)));
services.AddSingleton<PolicyService>();
services.AddSingleton<SharingService>();
services.AddSingleton<PublicLinkService>();
services.AddDataProtection(); // Cookies für eingegebene Link-Passwörter; Schlüssel flüchtig: nach einem Neustart fragt der Link erneut
services.AddSingleton<ItemService>();
services.AddSingleton<NotificationService>();
services.AddSingleton<IPortalStore>(sp =>
    new FilePortalStore(Path.Combine(sp.GetRequiredService<IOptions<ServerOptions>>().Value.DataDirectory, "portal")));
services.AddSingleton<PortalService>();
services.AddSingleton<IThumbnailRenderer, SkiaThumbnailRenderer>();
services.AddSingleton<IThumbnailCache>(sp =>
    new FileThumbnailCache(Path.Combine(sp.GetRequiredService<IOptions<ServerOptions>>().Value.DataDirectory, "thumbnails")));
services.AddSingleton<PreviewService>();
services.AddHostedService<AdminBootstrap>();
services.AddSingleton<ServerBackup>();
services.AddHostedService<BackupScheduler>();

var app = builder.Build();

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var requireTls = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value.RequireTls;
    if (requireTls && !context.Request.IsHttps && context.Request.Path != "/healthz")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(
            new ProblemDetail(ErrorCode.InvalidRequest, "TLS erforderlich."), context.RequestAborted);
        return;
    }

    await next();
});

// Weboberfläche (ADR-11): statische Dateien der Blazor-App. Strenge Sicherheitskopfzeilen: nur eigene Quellen, kein Einbetten in fremde
// Seiten; WebAssembly braucht 'wasm-unsafe-eval', Blazor setzt Stilattribute (Kontextmenü-Position).
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
    }

    await next();
});
app.UseBlazorFrameworkFiles();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapApi();
app.MapFallbackToFile("/s/{*token}", "index.html"); // öffentliche Links öffnen die Weboberfläche
app.Run();

/// <summary>Sichtbar für <c>WebApplicationFactory</c> in den Integrationstests.</summary>
public partial class Program;
