using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.Tray.Controls;
using SyncDrive.Client.Ui;
using SyncDrive.Infra.Ipc;

namespace SyncDrive.Client.Tray;

/// <summary>
/// Oberflächenprozess: Tray-Symbol mit Menü (Pause/Resume, Settings | About, Troubleshoot | Exit), Panel per Klick, Hauptfenster und Assistent
/// bei Bedarf. Der Daemon wird gestartet, wenn er nicht läuft; Sync läuft auch ohne geöffnete Fenster.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (Program.Arguments is ["--shell", var verb, var path, ..])
            {
                _ = RunShellCommandAsync(desktop, verb, path);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var icon = Badges.AppIcon();
            Control.LoadedEvent.AddClassHandler<Window>((window, _) => window.Icon ??= icon);
            var texts = Texts.For("system");
            var home = Environment.GetEnvironmentVariable("SYNCDRIVE_CLIENT_HOME") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncDrive");
            var windows = new WindowManager(
                desktop, texts, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncDrive"));
            var pipe = IpcProtocol.DefaultPipeName(Environment.UserName);
            var shell = new ShellViewModel(
                async ct => await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(2), ct),
                LaunchDaemon, windows, texts, Path.Combine(home, "logs"));
            windows.Attach(shell);
            SetUpTray(shell, windows, texts);
            var stop = new CancellationTokenSource();
            _ = shell.RunAsync(stop.Token);
            desktop.Exit += (_, _) =>
            {
                stop.Cancel();
                stop.Dispose();
            };
            if (!Program.Arguments.Contains("--background"))
            {
                shell.ConnectionChanged += OpenOnFirstConnect;

                void OpenOnFirstConnect()
                {
                    shell.ConnectionChanged -= OpenOnFirstConnect;
                    _ = OpenStartAsync(shell, windows);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Aktion aus dem Explorer-Menü: mit dem Daemon verbinden, in seiner Sprache ausführen, beenden.</summary>
    private static async Task RunShellCommandAsync(IClassicDesktopStyleApplicationLifetime desktop, string verb, string path)
    {
        var texts = Texts.For("system");
        try
        {
            await using var client = await IpcClient.ConnectAsync(IpcProtocol.DefaultPipeName(Environment.UserName), TimeSpan.FromSeconds(3), CancellationToken.None);
            texts = Texts.For((await client.GetSettingsAsync(CancellationToken.None)).Language);
            await new ShellCommand(client, new ShellModeUi(texts), texts).RunAsync(verb, path, CancellationToken.None);
        }
        catch (IpcException)
        {
            await new ShellModeUi(texts).ShowMessageAsync(texts.DaemonNotRunning);
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Erster Start ohne Aufgabe: Assistent; sonst die Aufgabenliste.</summary>
    private static async Task OpenStartAsync(ShellViewModel shell, WindowManager windows)
    {
        var tasks = shell.Client is { } client ? await client.GetTasksAsync(CancellationToken.None) : [];
        windows.Post(() =>
        {
            if (tasks.Count == 0)
            {
                windows.OpenWizard();
            }
            else
            {
                windows.OpenMainWindow(MainPage.SyncTasks);
            }

            // macOS: direkt aus dem Disk-Image gestartet – dort fehlt SyncDrive in „Programme“, und der Autostart bliebe aus
            if (OperatingSystem.IsMacOS() && SyncDrive.Client.Daemon.MacLaunchAgent.RunsFromDiskImage(AppContext.BaseDirectory))
            {
                _ = windows.ShowMessageAsync(shell.Texts.MoveToApplications);
            }
        });
    }

    /// <summary>
    /// Windows: Symbol im Infobereich, Klick öffnet das Panel, Rechtsklick das Menü. macOS: Symbol in der Menüleiste direkt über AppKit
    /// (<see cref="MacStatusItem"/>), jeder Klick öffnet das Panel; Pause, Fehlersuche und Beenden stehen dort im „⋯“ des Panels.
    /// </summary>
    private void SetUpTray(ShellViewModel shell, WindowManager windows, Texts texts)
    {
        var spin = 0;
        Action<int> showIcon;
        Action refreshTexts;
        if (OperatingSystem.IsMacOS())
        {
            (showIcon, refreshTexts) = MacTray(shell, windows);
        }
        else
        {
            var pause = new NativeMenuItem(shell.PauseMenuText) { Command = shell.TogglePauseCommand };
            var settings = new NativeMenuItem(texts.MenuSettings) { Command = shell.OpenSettingsCommand };
            var about = new NativeMenuItem(texts.MenuAbout) { Command = shell.AboutCommand };
            var troubleshoot = new NativeMenuItem(texts.MenuTroubleshoot) { Command = shell.TroubleshootCommand };
            var exit = new NativeMenuItem(texts.MenuExit) { Command = shell.ExitCommand };
            var menu = new NativeMenu { pause, settings, new NativeMenuItemSeparator(), about, troubleshoot, new NativeMenuItemSeparator(), exit };
            var tray = new TrayIcon { Menu = menu, ToolTipText = shell.Tooltip, IsVisible = true };
            tray.Clicked += (_, _) => windows.TogglePanel();
            TrayIcon.SetIcons(this, [tray]);
            showIcon = s => tray.Icon = TrayArt.Icon(shell, s);
            refreshTexts = () =>
            {
                tray.ToolTipText = shell.Tooltip;
                pause.Header = shell.PauseMenuText;
                settings.Header = shell.Texts.MenuSettings; // Sprache kann gewechselt haben
                about.Header = shell.Texts.MenuAbout;
                troubleshoot.Header = shell.Texts.MenuTroubleshoot;
                exit.Header = shell.Texts.MenuExit;
            };
        }

        var spinner = new DispatcherTimer(TimeSpan.FromMilliseconds(110), DispatcherPriority.Background, (_, _) =>
        {
            spin = (spin + 30) % 360; // zwölf Bilder je Umdrehung
            showIcon(spin);
        });
        void UpdateIcon()
        {
            showIcon(spin);
            spinner.IsEnabled = shell.TrayState == TrayState.Syncing; // kreisende Pfeile, solange abgeglichen wird
        }

        UpdateIcon();
        refreshTexts();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.Settings))
            {
                UpdateIcon(); // schlicht oder farbig
            }

            if (e.PropertyName is null or "" or nameof(ShellViewModel.TrayState) or nameof(ShellViewModel.HasNotifications)
                or nameof(ShellViewModel.IsConnected))
            {
                UpdateIcon();
                refreshTexts();
            }
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static (Action<int> ShowIcon, Action RefreshTexts) MacTray(ShellViewModel shell, WindowManager windows)
    {
        var item = new MacStatusItem();
        item.Clicked += () => Dispatcher.UIThread.Post(windows.TogglePanel);
        return (s => item.SetImage(TrayArt.MacImage(shell, s)), () => item.SetToolTip(shell.Tooltip));
    }

    /// <summary>Startet <c>syncdrive-daemon</c> aus dem Programmordner (als .exe oder, in der Entwicklung, per <c>dotnet</c>).</summary>
    private static void LaunchDaemon()
    {
        var folder = AppContext.BaseDirectory;
        var exe = Path.Combine(folder, OperatingSystem.IsWindows() ? "syncdrive-daemon.exe" : "syncdrive-daemon");
        var dll = Path.Combine(folder, "syncdrive-daemon.dll");
        var start = File.Exists(exe)
            ? new ProcessStartInfo(exe)
            : File.Exists(dll) ? new ProcessStartInfo("dotnet", $"\"{dll}\"") : null;
        if (start is null)
        {
            return;
        }

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        try
        {
            using var _ = Process.Start(start);
        }
        catch (Win32Exception)
        {
            // Start nicht möglich; die Oberfläche zeigt „Dienst läuft nicht“ und versucht es erneut
        }
    }
}
