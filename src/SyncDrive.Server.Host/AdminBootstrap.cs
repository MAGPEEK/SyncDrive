using Microsoft.Extensions.Options;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Server.Host;

/// <summary>Legt beim ersten Start das konfigurierte Admin-Konto an. Es gibt keinen Standardzugang.</summary>
public sealed partial class AdminBootstrap(
    IAccountStore store, AccountService accounts, IOptions<ServerOptions> options, ILogger<AdminBootstrap> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await store.AnyUserAsync(cancellationToken))
        {
            return;
        }

        var settings = options.Value;
        var password = settings.AdminPasswordFile is { Length: > 0 } file
            ? (await File.ReadAllTextAsync(file, cancellationToken)).Trim()
            : settings.AdminPassword;
        if (string.IsNullOrWhiteSpace(settings.AdminUserName) || string.IsNullOrEmpty(password))
        {
            LogNoAdminConfigured(logger);
            return;
        }

        var result = await accounts.CreateUserAsync(settings.AdminUserName, password, isAdmin: true, cancellationToken);
        if (result.IsSuccess)
        {
            LogAdminCreated(logger, settings.AdminUserName);
        }
        else
        {
            throw new InvalidOperationException($"Admin-Konto konnte nicht angelegt werden: {result.Error!.Message}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Keine Benutzer vorhanden und kein Admin konfiguriert (Server__AdminUserName, Server__AdminPassword).")]
    private static partial void LogNoAdminConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin-Konto '{UserName}' angelegt.")]
    private static partial void LogAdminCreated(ILogger logger, string userName);
}
