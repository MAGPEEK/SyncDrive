using System.Reflection;
using SyncDrive.Contracts;
using SyncDrive.Core.Paths;

namespace SyncDrive.Architecture.Tests;

/// <summary>Setzt die Abhängigkeitsregeln der hexagonalen Architektur durch.</summary>
public class DependencyRulesTests
{
    private static readonly string[] AllowedPrefixes = ["System", "netstandard", "mscorlib", "Microsoft.CSharp"];

    public static TheoryData<string> PureAssemblies() => new()
    {
        typeof(RelativePath).Assembly.GetName().Name!,
        typeof(ErrorCode).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(PureAssemblies))]
    public void Pure_assemblies_depend_only_on_the_framework(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName);

        var offenders = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !AllowedPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offenders);
    }

    public static TheoryData<string> InfraAssemblies() => new()
    {
        typeof(SyncDrive.Infra.Blob.FileBlockStore).Assembly.GetName().Name!,
        typeof(SyncDrive.Infra.Fs.LocalFileStore).Assembly.GetName().Name!,
        typeof(SyncDrive.Infra.Security.Argon2PasswordHasher).Assembly.GetName().Name!,
        typeof(SyncDrive.Infra.Sqlite.SqliteSpaceStore).Assembly.GetName().Name!,
        typeof(SyncDrive.Infra.Ipc.IpcServer).Assembly.GetName().Name!,
        typeof(SyncDrive.Infra.Secrets.DpapiCredentialStore).Assembly.GetName().Name!,
        typeof(SyncDrive.Infra.CloudFiles.WindowsCloudFolders).Assembly.GetName().Name!,
        typeof(SyncDrive.Infra.Imaging.SkiaThumbnailRenderer).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(InfraAssemblies))]
    public void Infra_projects_do_not_know_each_other(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName);

        var offenders = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("SyncDrive.Infra.", StringComparison.Ordinal) && n != assemblyName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Server_app_depends_only_on_core_contracts_and_the_framework()
    {
        var offenders = typeof(SyncDrive.Server.App.Spaces.SpaceService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !AllowedPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))
                && n is not ("SyncDrive.Core" or "SyncDrive.Contracts"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_does_not_reference_contracts() =>
        Assert.DoesNotContain(
            typeof(RelativePath).Assembly.GetReferencedAssemblies(),
            a => a.Name == typeof(ErrorCode).Assembly.GetName().Name);

    [Fact]
    public void Core_does_not_use_io_networking_or_database_namespaces()
    {
        var forbiddenNamespaces = new[] { "System.Net", "System.Data", "System.IO.Pipes", "System.Diagnostics.Process" };

        var offenders = typeof(RelativePath).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(f => f.FieldType)
                .Concat(t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))))
            .Where(t => t.Namespace is not null && forbiddenNamespaces.Any(ns => t.Namespace.StartsWith(ns, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }
}
