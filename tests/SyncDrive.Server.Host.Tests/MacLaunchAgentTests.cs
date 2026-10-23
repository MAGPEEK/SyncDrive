using System.Xml.Linq;
using SyncDrive.Client.Daemon;

namespace SyncDrive.Server.Host.Tests;

/// <summary>„Beim Anmelden öffnen“ unter macOS: eine LaunchAgent-Datei, die launchd beim Anmelden liest; hier in einem Temp-Ordner.</summary>
public sealed class MacLaunchAgentTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "syncdrive-launchagents-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Enabling_writes_a_launch_agent_that_starts_the_app_in_the_background_and_disabling_removes_it()
    {
        var agent = new MacLaunchAgent("/Applications/SyncDrive.app/Contents/MacOS/syncdrive", _folder);

        agent.Apply(true);
        agent.Apply(true); // erneut: ersetzt, kein Fehler

        var plist = XDocument.Load(agent.PlistPath);
        var values = plist.Root!.Element("dict")!.Elements().Select(e => e.Name.LocalName == "array"
            ? string.Join(" ", e.Elements().Select(s => s.Value))
            : e.Name.LocalName is "true" ? "true" : e.Value).ToList();
        Assert.Equal(Path.Combine(_folder, "org.syncdrive.client.plist"), agent.PlistPath);
        Assert.Equal(
            ["Label", "org.syncdrive.client", "ProgramArguments", "/Applications/SyncDrive.app/Contents/MacOS/syncdrive --background",
             "RunAtLoad", "true", "ProcessType", "Interactive"],
            values);
        Assert.Equal("plist", plist.DocumentType!.Name);

        agent.Apply(false);
        agent.Apply(false);
        Assert.False(File.Exists(agent.PlistPath));
    }
}
