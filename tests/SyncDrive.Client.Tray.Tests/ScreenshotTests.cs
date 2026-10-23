using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.Tray.Controls;
using SyncDrive.Client.Tray.Views;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray.Tests;

/// <summary>
/// Rendert alle Fenster mit Beispieldaten zu PNG (Ordner <c>SYNCDRIVE_SCREENSHOTS</c>, sonst Temp) zur Durchsicht
/// und prüft dabei, dass Inhalte wirklich erscheinen.
/// </summary>
public sealed class ScreenshotTests
{
    private static readonly string Folder = Environment.GetEnvironmentVariable("SYNCDRIVE_SCREENSHOTS") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Path.GetTempPath(), "syncdrive-screens");

    private static (ShellViewModel Shell, DemoDaemon Daemon) Shell(TrayState state, Texts? texts = null)
    {
        var daemon = new DemoDaemon();
        var shell = new ShellViewModel(_ => Task.FromResult<IDaemonClient>(daemon), () => { }, new SilentUi(), texts ?? Texts.English, "logs");
        shell.UseClient(daemon);
        shell.ApplyStatus(DemoDaemon.Status(state));
        return (shell, daemon);
    }

    private static void Save(Window window, string name)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(Folder);
        frame!.Save(Path.Combine(Folder, name + ".png"));
        window.Close();
    }

    private static T Find<T>(Window window, Func<T, bool>? where = null)
        where T : class => window.GetVisualDescendants().OfType<T>().First(where ?? (_ => true));

    [AvaloniaTheory]
    [InlineData(TrayState.UpToDate)]
    [InlineData(TrayState.Syncing)]
    [InlineData(TrayState.Paused)]
    [InlineData(TrayState.Error)]
    public async Task Panel(TrayState state)
    {
        var (shell, _) = Shell(state);
        await shell.Panel.RefreshAsync(CancellationToken.None);
        var window = new PanelWindow { DataContext = shell.Panel };

        Save(window, $"panel-{state.ToString().ToLowerInvariant()}");

        Assert.Equal(5, shell.Panel.Recent.Count); // die bewusst ausgelassene Office-Sperrdatei steht nur im Protokoll
    }

    [AvaloniaFact]
    public async Task Panel_in_german_with_notifications_tab()
    {
        var (shell, _) = Shell(TrayState.UpToDate, Texts.German);
        await shell.Panel.RefreshAsync(CancellationToken.None);
        shell.Panel.SelectNotificationsCommand.Execute(null);

        Save(new PanelWindow { DataContext = shell.Panel }, "panel-notifications-de");
    }

    [AvaloniaTheory]
    [InlineData(MainPage.SyncTasks, TrayState.Error)]
    [InlineData(MainPage.SyncTasks, TrayState.Syncing)]
    [InlineData(MainPage.Logs, TrayState.UpToDate)]
    [InlineData(MainPage.Notifications, TrayState.UpToDate)]
    [InlineData(MainPage.GlobalSettings, TrayState.UpToDate)]
    public async Task Main_window(MainPage page, TrayState state)
    {
        var (shell, _) = Shell(state);
        var model = new MainWindowViewModel(shell, new SilentUi()) { Page = page };
        await model.RefreshAsync();
        model.SyncTasks.SelectedItem = model.SyncTasks.Groups[0].Tasks[0];

        Save(new MainWindow { DataContext = model }, $"main-{page.ToString().ToLowerInvariant()}-{state.ToString().ToLowerInvariant()}");

        Assert.Equal(5, model.SyncTasks.Items.Count); // 2 Servergruppen + 3 Aufgaben
        Assert.Equal(6, model.Logs.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Reloading_the_logs_leaves_the_filter_boxes_without_errors()
    {
        var (shell, _) = Shell(TrayState.UpToDate, Texts.German);
        var model = new MainWindowViewModel(shell, new SilentUi()) { Page = MainPage.Logs };
        await model.RefreshAsync();
        var window = new MainWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await model.Logs.LoadAsync(); // leert und füllt die Aufgabenliste der Auswahlbox
        Dispatcher.UIThread.RunJobs();

        var boxes = window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(boxes);
        Assert.All(boxes, box => Assert.False(DataValidationErrors.GetHasErrors(box), string.Join(" | ", DataValidationErrors.GetErrors(box) ?? [])));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1, "display")]
    [InlineData(3, "proxy")]
    [InlineData(4, "usage")]
    public async Task Settings_tabs(int tab, string name)
    {
        var (shell, _) = Shell(TrayState.UpToDate);
        var model = new MainWindowViewModel(shell, new SilentUi()) { Page = MainPage.GlobalSettings };
        await model.RefreshAsync();
        var window = new MainWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Find<TabControl>(window).SelectedIndex = tab == 1 ? 2 : tab;
        if (name == "proxy")
        {
            model.Settings.ProxyManual = true;
        }

        Save(window, "settings-" + name);
    }

    [AvaloniaFact]
    public async Task Main_window_without_tasks_shows_the_empty_state()
    {
        var (shell, daemon) = Shell(TrayState.Normal);
        daemon.Tasks.Clear();
        var model = new MainWindowViewModel(shell, new SilentUi());
        await model.RefreshAsync();

        Save(new MainWindow { DataContext = model }, "main-synctasks-empty");

        Assert.True(model.SyncTasks.IsEmpty);
    }

    [AvaloniaTheory]
    [InlineData("connection-existing")]
    [InlineData("connection-new")]
    [InlineData("checking")]
    [InlineData("certificate")]
    [InlineData("folders-empty")]
    [InlineData("folders-chosen")]
    [InlineData("options")]
    public async Task Wizard(string step)
    {
        var daemon = new DemoDaemon();
        var model = new WizardViewModel(daemon, new SilentUi(), Texts.English, @"C:\Users\Beispiel\SyncDrive");
        await model.LoadAsync(CancellationToken.None);
        switch (step)
        {
            case "connection-new":
                model.UseExisting = false;
                model.Address = "nas.example";
                model.UserName = "anna";
                model.Password = "geheim";
                break;
            case "checking":
                model.UseExisting = false;
                model.Address = "nas.example";
                model.IsBusy = true;
                break;
            case "certificate":
                model.UseExisting = false;
                model.Address = "nas.example";
                model.CertificateFingerprint = "3f9a71c208be5d44e1a09c37b6f22d18a4c50e9377b1c8d615fa62e09b3d4c71";
                break;
            case "folders-empty":
                await model.NextCommand.ExecuteAsync(null);
                break;
            case "folders-chosen":
                await model.NextCommand.ExecuteAsync(null);
                await model.ChooseServerFolderCommand.ExecuteAsync(null);
                await model.ChooseLocalFolderCommand.ExecuteAsync(null);
                break;
            case "options":
                await model.NextCommand.ExecuteAsync(null);
                await model.ChooseServerFolderCommand.ExecuteAsync(null);
                await model.ChooseLocalFolderCommand.ExecuteAsync(null);
                await model.NextCommand.ExecuteAsync(null);
                model.UploadOnly = true;
                break;
        }

        Save(new WizardWindow(model), "wizard-" + step);
    }

    [AvaloniaFact]
    public async Task Server_folder_dialog_and_rule_message()
    {
        var picker = new ServerFolderPickerViewModel(new DemoDaemon(), "c1", Texts.English);
        await picker.LoadAsync(CancellationToken.None);
        await picker.Roots[0].LoadChildrenAsync();
        picker.Roots[0].IsExpanded = true;
        picker.Selected = picker.Roots[0].Children[1];

        Save(new ServerFolderDialog(picker), "dialog-server-folder");
        Save(new MessageDialog(Texts.English.FolderOverlaps, Texts.English, confirm: false), "dialog-rule");
        Save(new MessageDialog(Texts.English.ConfirmDeleteTask, Texts.English, confirm: true), "dialog-confirm");
    }

    [AvaloniaTheory]
    [InlineData(0, "folder")]
    [InlineData(1, "filter")]
    [InlineData(2, "mode")]
    public async Task Sync_rules(int tab, string name)
    {
        var daemon = new DemoDaemon();
        var rules = new SyncRulesViewModel(daemon, new SilentUi(), Texts.English, (await daemon.GetTaskAsync("t1", CancellationToken.None))!);
        await rules.LoadAsync(CancellationToken.None);
        var window = new SyncRulesWindow(rules);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Find<TabControl>(window).SelectedIndex = tab;

        Save(window, "dialog-syncrules-" + name);

        Assert.Equal([true, false, true], rules.Folders.Select(f => f.IsChecked)); // Fotos ist ausgeschlossen
    }

    [AvaloniaFact]
    public void Edit_connection_and_toast()
    {
        var dialog = new EditConnectionViewModel(new DemoDaemon(), new ConnectionSummary("c1", "Heim-NAS", "nas.example:8443", "anna"), Texts.English)
        {
            Error = Texts.English.ErrorSignIn,
        };

        Save(new EditConnectionWindow(dialog), "dialog-editconnection");
        Save(new ToastWindow("SyncDrive", "ben shared Familie"), "toast");
    }

    [AvaloniaFact]
    public async Task Notifications_in_panel_and_main_window_and_usage_values()
    {
        var (shell, _) = Shell(TrayState.UpToDate);
        await shell.Panel.RefreshAsync(CancellationToken.None);
        await shell.Panel.SelectNotificationsCommand.ExecuteAsync(null);
        Save(new PanelWindow { DataContext = shell.Panel }, "panel-notifications");

        var model = new MainWindowViewModel(shell, new SilentUi()) { Page = MainPage.Notifications };
        await model.RefreshAsync();
        Save(new MainWindow { DataContext = model }, "main-notifications-list");

        model.Page = MainPage.GlobalSettings;
        var window = new MainWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Find<TabControl>(window).SelectedIndex = 4;
        Save(window, "settings-usage-values");

        Assert.Equal(2, model.Notifications.Items.Count);
        Assert.Contains(model.Settings.Usage.SelectMany(g => g.Tasks), t => t.Detail?.Contains("GB", StringComparison.Ordinal) == true);
    }

    [AvaloniaFact]
    public async Task Previous_versions_from_the_explorer_menu()
    {
        var versions = new VersionsViewModel(new DemoDaemon(), new SilentUi(), Texts.English, @"C:\Users\Beispiel\SyncDrive\Angebote\Angebot.docx");
        await versions.LoadAsync(CancellationToken.None);

        Save(new VersionsWindow(versions), "dialog-versions");

        Assert.Equal(3, versions.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Main_window_in_german()
    {
        var (shell, _) = Shell(TrayState.Syncing);
        shell.ApplySettings(new SyncDrive.Client.App.Settings.ClientSettings { Language = "de" });
        var model = new MainWindowViewModel(shell, new SilentUi());
        await model.RefreshAsync();

        Save(new MainWindow { DataContext = model }, "main-synctasks-de");

        Assert.Equal("Sync-Aufgaben", model.Texts.NavSyncTasks);
    }

    [AvaloniaFact]
    public void Tray_icons_for_every_state()
    {
        var states = new (TrayState State, bool Notify, string Name)[]
        {
            (TrayState.Normal, false, "normal"), (TrayState.UpToDate, false, "uptodate"), (TrayState.Syncing, false, "syncing"),
            (TrayState.Paused, false, "pause"), (TrayState.Error, false, "abnormal"), (TrayState.Disconnected, false, "disconnect"),
            (TrayState.UpToDate, true, "notification"),
        };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 18, Margin = new(16) };
        foreach (var (state, notify, name) in states)
        {
            var big = Badges.TrayBitmap(state, notify, minimal: false);
            row.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new Image { Source = big, Width = 64, Height = 64 },
                    new Border
                    {
                        Background = Brushes.Black, // dunkle Taskleiste, echte Größen 16 / 24 / 32 Pixel
                        Padding = new(4),
                        Child = new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 4,
                            Children =
                            {
                                new Image { Source = Badges.TrayBitmap(state, notify, minimal: true, size: 16, spin: 45), Width = 16, Height = 16 },
                                new Image { Source = Badges.TrayBitmap(state, notify, minimal: true, size: 24), Width = 24, Height = 24 },
                                new Image { Source = Badges.TrayBitmap(state, notify, minimal: true, size: 32), Width = 32, Height = 32 },
                            },
                        },
                    },
                    new Image { Source = Badges.TrayBitmap(state, notify, minimal: false, size: 24), Width = 24, Height = 24 },
                    new Border
                    {
                        Background = Brushes.WhiteSmoke, // macOS-Menüleiste (hell); das Vorlagenbild färbt macOS selbst
                        Padding = new(4),
                        Child = new Image { Source = Badges.TemplateTrayBitmap(state, notify, 36, spin: 45), Width = 18, Height = 18 },
                    },
                    new TextBlock { Text = name, FontSize = 11, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                },
            });
        }

        var badges = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14, Margin = new(16, 0, 16, 16) };
        foreach (var kind in Enum.GetValues<BadgeKind>().Skip(1))
        {
            badges.Children.Add(new FolderBadge { Kind = kind, Width = 40, Height = 40 });
        }

        Save(new Window { Width = 760, Height = 340, Content = new StackPanel { Children = { row, badges } } }, "tray-icons");
    }
}
