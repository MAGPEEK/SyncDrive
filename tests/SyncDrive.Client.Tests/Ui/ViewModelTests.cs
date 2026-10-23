using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Ui;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;
using TaskStatus = SyncDrive.Client.App.Status.TaskStatus;

namespace SyncDrive.Client.Tests.Ui;

/// <summary>Oberflächenlogik ohne Fenster: Zustände, Texte, Abläufe des Assistenten, Listen und Einstellungen.</summary>
public sealed class ViewModelTests : IAsyncDisposable
{
    private static readonly Texts En = Texts.English;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeDaemon _daemon = new();
    private readonly FakeUi _ui = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _shellRun;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_shellRun is not null)
        {
            await _shellRun.WaitAsync(TimeSpan.FromSeconds(10));
        }

        _stop.Dispose();
    }

    private static TaskSummary Task(string id, string server = "NAS", string local = @"C:\Sync\Drive", SyncDirection direction = SyncDirection.TwoWay) =>
        new(id, "c-" + server, server, server.ToLowerInvariant() + ":8443", "s1", string.Empty, local, direction, false, false);

    private static OverallStatus Status(params (string Id, TaskState State)[] tasks) =>
        OverallStatus.Combine([.. tasks.Select(t => new TaskStatus(t.Id, t.State, 0, null, [], t.State == TaskState.Disabled ? "Bitte anmelden" : null, null))], false);

    private async Task<ShellViewModel> ConnectedShellAsync()
    {
        var shell = new ShellViewModel(_ => System.Threading.Tasks.Task.FromResult<IDaemonClient>(_daemon), () => { }, _ui, En, "logs");
        _shellRun = shell.RunAsync(_stop.Token);
        await EventuallyAsync(() => shell.IsConnected);
        return shell;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Zeitüberschreitung");
            await System.Threading.Tasks.Task.Delay(20);
        }
    }

    [Fact]
    public void Relative_times_use_the_plural_bracket_and_names_are_shortened_in_the_middle()
    {
        Assert.Equal("just now", Presentation.RelativeTime(Now.AddSeconds(-20), Now, En));
        Assert.Equal("5 minute(s) ago", Presentation.RelativeTime(Now.AddMinutes(-5), Now, En));
        Assert.Equal("5 hour(s) ago", Presentation.RelativeTime(Now.AddHours(-5.5), Now, En));
        Assert.Equal("vor 3 Tag(en)", Presentation.RelativeTime(Now.AddDays(-3), Now, Texts.German));
        Assert.Equal("Anfang-…e.docx", Presentation.MiddleEllipsis("Anfang-sehr-langer-Name-Ende.docx", 14));
        Assert.Equal((FileKind.Pdf, FileKind.Folder, FileKind.Other), (Presentation.KindOf("a.PDF", false), Presentation.KindOf("a.pdf", true), Presentation.KindOf("a", false)));
    }

    [Fact]
    public void Unsynced_entries_carry_the_suffix()
    {
        var entry = new ActivityEntry(Now, ActivityAction.Upload, RelativePath.Parse("~$brief.docx"), false, ActivityResult.Unsynced, "filter");

        Assert.Equal("Uploaded – Unsynced", Presentation.Action(entry, En));
    }

    [Fact]
    public async Task Without_a_daemon_the_tray_shows_disconnected_and_starts_it_then_connects()
    {
        var attempts = 0;
        var launched = 0;
        var shell = new ShellViewModel(
            _ => ++attempts < 2 ? throw new IOException("kein Daemon") : System.Threading.Tasks.Task.FromResult<IDaemonClient>(_daemon),
            () => launched++, _ui, En, "logs");
        _shellRun = shell.RunAsync(_stop.Token);

        await EventuallyAsync(() => launched == 1);
        Assert.Equal((TrayState.Disconnected, "The sync service is not running"), (shell.TrayState, shell.StateWord));

        await EventuallyAsync(() => shell.IsConnected);
        _daemon.Publish(Status(("t1", TaskState.Done)));
        await EventuallyAsync(() => shell.TrayState == TrayState.UpToDate);
        Assert.Equal(("SyncDrive – Up-to-date", StatusColor.Green), (shell.Tooltip, shell.StateColor));
    }

    [Fact]
    public async Task The_tray_menu_offers_pause_or_resume_for_all_tasks()
    {
        var shell = await ConnectedShellAsync();
        _daemon.Publish(Status(("t1", TaskState.Done)));
        await EventuallyAsync(() => shell.TrayState == TrayState.UpToDate);
        Assert.Equal("Pause", shell.PauseMenuText);

        await shell.TogglePauseCommand.ExecuteAsync(null);
        _daemon.Publish(Status(("t1", TaskState.Paused)));
        await EventuallyAsync(() => shell.IsPaused);
        Assert.Equal("Resume", shell.PauseMenuText);
        await shell.TogglePauseCommand.ExecuteAsync(null);

        Assert.Equal(["pause *", "resume *"], _daemon.Calls);
    }

    [Fact]
    public async Task A_lost_daemon_turns_the_tray_disconnected()
    {
        var shell = await ConnectedShellAsync();
        _daemon.Publish(Status(("t1", TaskState.Done)));
        await EventuallyAsync(() => shell.TrayState == TrayState.UpToDate);

        _daemon.Drop();

        await EventuallyAsync(() => shell.TrayState == TrayState.Disconnected);
    }

    [Fact]
    public async Task The_panel_lists_recent_changes_with_action_time_and_dims_deletions()
    {
        _daemon.Tasks.Add(Task("t1"));
        _daemon.Activity.Add(new("t1", new(DateTimeOffset.UtcNow.AddHours(-5.2), ActivityAction.Download, RelativePath.Parse("Berichte/q3.pdf"), false, ActivityResult.Done)));
        _daemon.Activity.Add(new("t1", new(DateTimeOffset.UtcNow.AddMinutes(-2), ActivityAction.Delete, RelativePath.Parse("alt.txt"), false, ActivityResult.Done)));
        var shell = await ConnectedShellAsync();

        await shell.Panel.RefreshAsync(CancellationToken.None);

        Assert.Collection(
            shell.Panel.Recent,
            deleted => Assert.Equal(("alt.txt", "Deleted / 2 minute(s) ago", true), (deleted.Name, deleted.Detail, deleted.Dimmed)),
            pdf => Assert.Equal(("q3.pdf", "Downloaded / 5 hour(s) ago", FileKind.Pdf, false), (pdf.Name, pdf.Detail, pdf.Kind, pdf.Dimmed)));
        shell.Panel.Recent[1].Open!.Execute(null);
        Assert.Equal(Path.Combine(@"C:\Sync\Drive", "Berichte"), Assert.Single(_ui.Opened));
    }

    [Fact]
    public async Task Sync_tasks_are_grouped_by_server_show_their_state_and_can_be_paused_and_deleted()
    {
        _daemon.Tasks.AddRange([Task("t1", "NAS"), Task("t2", "NAS", @"C:\Sync\Fotos", SyncDirection.DownloadOnly), Task("t3", "Büro", @"D:\Akten")]);
        var shell = await ConnectedShellAsync();
        var main = new MainWindowViewModel(shell, _ui);
        _daemon.Publish(Status(("t1", TaskState.Done), ("t2", TaskState.Paused), ("t3", TaskState.Disabled)));
        await EventuallyAsync(() => shell.Status is not null);
        await main.SyncTasks.LoadAsync();

        Assert.Equal(["NAS", "Büro"], main.SyncTasks.Groups.Select(g => g.Name));
        var fotos = main.SyncTasks.Groups[0].Tasks[1];
        Assert.Equal(("Fotos", "(Download only)", "Paused"), (fotos.Title, fotos.ModeText, fotos.StateText));
        Assert.Equal("Bitte anmelden", main.SyncTasks.Groups[1].Tasks[0].Detail);

        main.SyncTasks.Selected = fotos;
        Assert.Equal("Resume", main.SyncTasks.PauseText);
        await main.SyncTasks.PauseOrResumeCommand.ExecuteAsync(null);
        main.SyncTasks.Selected = main.SyncTasks.Groups[1].Tasks[0];
        await main.SyncTasks.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["resume t2", "delete t3"], _daemon.Calls);
        Assert.Equal(["NAS"], main.SyncTasks.Groups.Select(g => g.Name));
        Assert.Contains(En.ConfirmDeleteTask, _ui.Messages);
    }

    [Fact]
    public async Task Without_tasks_the_list_shows_the_empty_state()
    {
        var shell = await ConnectedShellAsync();
        var main = new MainWindowViewModel(shell, _ui);

        await main.SyncTasks.LoadAsync();

        Assert.True(main.SyncTasks.IsEmpty);
    }

    [Fact]
    public async Task Logs_filter_by_task_event_and_search()
    {
        _daemon.Tasks.AddRange([Task("t1"), Task("t2", local: @"C:\Sync\Fotos")]);
        _daemon.Activity.Add(new("t1", new(Now, ActivityAction.Upload, RelativePath.Parse("a/bericht.docx"), false, ActivityResult.Done)));
        _daemon.Activity.Add(new("t1", new(Now.AddMinutes(-1), ActivityAction.Upload, RelativePath.Parse("~$bericht.docx"), false, ActivityResult.Unsynced)));
        _daemon.Activity.Add(new("t2", new(Now.AddMinutes(-2), ActivityAction.Download, RelativePath.Parse("urlaub.jpg"), false, ActivityResult.Done)));
        var shell = await ConnectedShellAsync();
        var logs = new MainWindowViewModel(shell, _ui).Logs;
        await logs.LoadAsync();

        Assert.Equal(3, logs.Rows.Count);
        Assert.Equal(["All tasks", "Drive", "Fotos"], logs.TaskOptions.Select(o => o.Value));

        logs.EventFilter = EventFilter.Unsynced;
        Assert.Equal("Uploaded – Unsynced", Assert.Single(logs.Rows).Event);

        logs.EventFilter = EventFilter.All;
        logs.TaskFilter = logs.TaskOptions[2];
        Assert.Equal("urlaub.jpg", Assert.Single(logs.Rows).Name);

        logs.TaskFilter = logs.TaskOptions[0];
        logs.Search = "BERICHT";
        Assert.Equal(2, logs.Rows.Count);

        // Beim Neuladen leert die Auswahlbox ihre Liste und meldet „nichts gewählt“ (null): das darf nicht scheitern
        logs.Search = string.Empty;
        logs.EventFilter = EventFilter.Download;
        logs.TaskFilter = null;
        logs.EventChoice = null;
        await logs.LoadAsync();
        Assert.Equal((EventFilter.Download, "All tasks", "urlaub.jpg"), (logs.EventFilter, logs.TaskFilter?.Value, Assert.Single(logs.Rows).Name));
    }

    [Fact]
    public async Task Settings_load_without_saving_and_every_change_is_saved_as_client_settings()
    {
        _daemon.Settings = new ClientSettings { RenameDiscarded = false, Proxy = new(ProxyMode.Manual, "proxy", 3128) };
        var shell = await ConnectedShellAsync();
        var settings = new MainWindowViewModel(shell, _ui).Settings;

        await settings.LoadAsync();
        Assert.Empty(_daemon.SavedSettings);
        Assert.True(settings.IsProxyManual);

        settings.KeepServer = true;
        settings.ProxyNone = true;

        var saved = _daemon.SavedSettings[^1];
        Assert.Equal((ConflictStrategy.ServerWins, false, ProxyMode.None, "proxy"), (saved.Conflict, saved.RenameDiscarded, saved.Proxy.Mode, saved.Proxy.Host));
    }

    [Fact]
    public async Task The_wizard_preselects_a_previous_connection_and_goes_to_folders()
    {
        _daemon.Connections.Add(new("c1", "NAS", "nas:8443", "anna"));
        var wizard = new WizardViewModel(_daemon, _ui, En, @"C:\Users\x\SyncDrive");
        await wizard.LoadAsync(CancellationToken.None);

        Assert.True(wizard.UseExisting);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Folders, wizard.Step);
        Assert.Empty(_daemon.SignIns);
        Assert.Equal("Not yet selected", wizard.ServerFolderText);
    }

    [Fact]
    public async Task A_new_connection_asks_to_trust_an_unknown_certificate_and_reports_wrong_passwords()
    {
        var wizard = new WizardViewModel(_daemon, _ui, En, @"C:\Users\x\SyncDrive");
        await wizard.LoadAsync(CancellationToken.None);
        Assert.False(wizard.UseExisting);
        wizard.Address = "nas.local";
        wizard.UserName = "anna";
        wizard.Password = "falsch";
        _daemon.SignInResults.Enqueue(new SignInOutcome(null, SetupErrors.UntrustedCertificate, "x", "ab12"));
        _daemon.SignInResults.Enqueue(new SignInOutcome(null, SetupErrors.SignInFailed, "x"));
        _daemon.SignInResults.Enqueue(new SignInOutcome("c-neu"));

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal("ab12", wizard.CertificateFingerprint);
        Assert.Equal("ab12", wizard.FingerprintText);

        await wizard.TrustCertificateCommand.ExecuteAsync(null);
        Assert.Equal((null as string, En.ErrorSignIn), (wizard.CertificateFingerprint, wizard.Error));

        wizard.Password = "richtig";
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Folders, wizard.Step);
        Assert.Equal([null, "ab12", null], _daemon.SignIns.Select(s => s.TrustedCertificateSha256));
        Assert.Equal(string.Empty, wizard.Password); // nicht länger im Speicher als nötig
    }

    [Fact]
    public async Task A_rejected_local_folder_shows_the_reason_and_the_task_is_created_with_the_options()
    {
        _daemon.Connections.Add(new("c1", "NAS", "nas:8443", "anna"));
        _daemon.Folders[":"] = [new("s1", string.Empty, "My Drive", true, true)];
        _daemon.CheckFolder = path => path.EndsWith("Belegt", StringComparison.Ordinal) ? [TaskRuleViolation.LocalFolderOverlapsTask] : [];
        _ui.ServerFolderAnswer = async picker =>
        {
            picker.Selected = picker.Roots[0];
            await System.Threading.Tasks.Task.Yield();
            return picker.Result;
        };
        var wizard = new WizardViewModel(_daemon, _ui, En, @"C:\Users\x\SyncDrive");
        var finished = (bool?)null;
        wizard.Finished += ok => finished = ok;
        await wizard.LoadAsync(CancellationToken.None);
        await wizard.NextCommand.ExecuteAsync(null);

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(En.ErrorChooseFolders, wizard.Error);

        await wizard.ChooseServerFolderCommand.ExecuteAsync(null);
        _ui.LocalFolderAnswer = @"C:\Belegt";
        await wizard.ChooseLocalFolderCommand.ExecuteAsync(null);
        Assert.Equal((En.FolderOverlaps, @"C:\Users\x\SyncDrive (created automatically)"), (Assert.Single(_ui.Messages), wizard.LocalFolderText));

        _ui.LocalFolderAnswer = @"C:\Frei";
        await wizard.ChooseLocalFolderCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal((WizardStep.Options, "Done", "My Drive"), (wizard.Step, wizard.NextText, wizard.ServerFolderText));

        wizard.Direction = SyncDirection.UploadOnly;
        wizard.MaxFileSizeMb = 2;
        wizard.ExcludedExtensions = "*.iso, tmp";
        await wizard.NextCommand.ExecuteAsync(null);

        var created = Assert.Single(_daemon.Created);
        Assert.Equal(("c1", "s1", string.Empty, @"C:\Frei", SyncDirection.UploadOnly, true), (created.ConnectionId, created.SpaceId, created.RemotePath, created.LocalPath, created.Direction, created.KeepLocallyDeletedOnServer));
        Assert.Equal(2L * 1024 * 1024, created.Filters!.MaxFileSizeBytes);
        Assert.Equal(["iso", "tmp"], created.Filters.ExcludedExtensions!);
        Assert.True(created.AdvancedConsistencyCheck);
        Assert.True(finished);
    }

    [Fact]
    public async Task Without_a_chosen_local_folder_the_wizard_takes_a_free_SyncDrive_folder_in_the_profile()
    {
        _daemon.Connections.Add(new("c1", "NAS", "nas:8443", "anna"));
        _daemon.Folders[":"] = [new("s1", string.Empty, "My Drive", true, true)];
        _daemon.CheckFolder = path => path == @"C:\Users\x\SyncDrive" ? [TaskRuleViolation.LocalFolderOverlapsTask] : []; // schon eine Aufgabe
        _ui.ServerFolderAnswer = async picker =>
        {
            picker.Selected = picker.Roots[0];
            await System.Threading.Tasks.Task.Yield();
            return picker.Result;
        };
        var wizard = new WizardViewModel(_daemon, _ui, En, @"C:\Users\x\SyncDrive");
        await wizard.LoadAsync(CancellationToken.None);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.ChooseServerFolderCommand.ExecuteAsync(null);

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Users\x\SyncDrive 2", Assert.Single(_daemon.Created).LocalPath);
    }

    /// <summary>macOS bis zur File-Provider-Erweiterung: keine Dateien auf Abruf, auch wenn das Häkchen noch gesetzt wäre.</summary>
    [Fact]
    public async Task Where_files_on_demand_are_not_available_the_wizard_never_creates_such_a_task()
    {
        _daemon.Connections.Add(new("c1", "NAS", "nas:8443", "anna"));
        _daemon.Folders[":"] = [new("s1", string.Empty, "My Drive", true, true)];
        _ui.ServerFolderAnswer = async picker =>
        {
            picker.Selected = picker.Roots[0];
            await System.Threading.Tasks.Task.Yield();
            return picker.Result;
        };
        var wizard = new WizardViewModel(_daemon, _ui, En, @"C:\Users\x\SyncDrive") { OnDemandAvailable = false };
        await wizard.LoadAsync(CancellationToken.None);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.ChooseServerFolderCommand.ExecuteAsync(null);
        wizard.OnDemand = true;

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(_daemon.Created).OnDemand);
    }

    [Fact]
    public async Task Sync_rules_show_the_task_and_unchecking_a_folder_excludes_it_with_its_subfolders()
    {
        var task = new SyncTaskDefinition("t1", "c1", "s1", RelativePath.Parse("Team"), @"C:\Sync\Team",
            Filters: new TaskFilterSettings(5L * 1024 * 1024, ["Thumbs.db"], ["iso"], [RelativePath.Parse("Alt")]));
        _daemon.Folders["s1:Team"] = [new("s1", "Team/Alt", "Alt", false, true), new("s1", "Team/Fotos", "Fotos", false, true)];
        _daemon.Folders["s1:Team/Fotos"] = [new("s1", "Team/Fotos/2026", "2026", false, true)];
        var rules = new SyncRulesViewModel(_daemon, _ui, En, task);
        await rules.LoadAsync(CancellationToken.None);

        Assert.Equal([("Alt", false), ("Fotos", true)], rules.Folders.Select(f => (f.Name, f.IsChecked)));
        Assert.Equal((5m, "iso", "Thumbs.db"), (rules.MaxFileSizeMb, rules.ExcludedExtensions, rules.ExcludedNames));

        var fotos = rules.Folders[1];
        await fotos.LoadChildrenAsync();
        fotos.Children[0].IsChecked = false; // erst nur 2026 ausschließen …
        fotos.IsChecked = false;             // … dann ganz Fotos: 2026 geht darin auf
        rules.Folders[0].IsChecked = true;   // Alt wieder synchronisieren
        rules.UploadOnly = true;
        rules.ExcludedExtensions = "*.iso, *.tmp";
        await rules.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(_daemon.Updated);
        Assert.Equal(["Fotos"], saved.Filters!.ExcludedFolders!.Select(f => f.Value));
        Assert.Equal(["iso", "tmp"], saved.Filters.ExcludedExtensions!);
        Assert.Equal((SyncDirection.UploadOnly, false), (saved.Direction, fotos.Children[0].IsEnabled));
    }

    [Fact]
    public async Task Edit_connection_signs_in_again_and_asks_about_new_certificates()
    {
        _daemon.ReSignInResults.Enqueue(new SignInOutcome(null, SetupErrors.UntrustedCertificate, "x", "cafe0123"));
        _daemon.ReSignInResults.Enqueue(new SignInOutcome("c1"));
        var dialog = new EditConnectionViewModel(_daemon, new ConnectionSummary("c1", "NAS", "nas:8443", "anna"), En);
        var finished = (bool?)null;
        dialog.Finished += ok => finished = ok;
        Assert.Equal(("nas:8443", "anna"), (dialog.Address, dialog.UserName));

        dialog.Address = "nas-neu:8443";
        dialog.Password = "pw";
        await dialog.SaveCommand.ExecuteAsync(null);
        Assert.Equal("cafe 0123", dialog.FingerprintText);
        await dialog.TrustCertificateCommand.ExecuteAsync(null);

        Assert.True(finished);
        Assert.Equal([(string?)null, "cafe0123"], _daemon.SignIns.Select(s => s.TrustedCertificateSha256));
        Assert.Equal("nas-neu:8443", _daemon.SignIns[^1].ServerAddress);
    }

    [Fact]
    public async Task The_panel_lists_notifications_marks_them_read_and_announces_new_ones()
    {
        _daemon.Notifications.Add(new("n1", "c1", "NAS", "shared", DateTimeOffset.UtcNow.AddMinutes(-30), "ben", "Familie", string.Empty, false));
        var shell = await ConnectedShellAsync();
        shell.ApplySettings(new ClientSettings());
        await shell.Panel.RefreshAsync(CancellationToken.None);

        var note = Assert.Single(shell.Panel.Notifications);
        Assert.Equal(("ben", "shared", "Familie", "NAS", true), (note.Actor, note.Verb, note.Target, note.Server, note.Unread));
        Assert.Empty(_ui.Toasts); // was beim Start schon da ist, wird nicht als neu gemeldet

        await shell.Panel.SelectNotificationsCommand.ExecuteAsync(null);
        Assert.Contains("read notifications", _daemon.Calls);

        _daemon.Notifications.Add(new("n2", "c1", "NAS", "shared", DateTimeOffset.UtcNow, "carla", "Projekte", string.Empty, false));
        _daemon.Activity.Add(new("t1", new(DateTimeOffset.UtcNow, ActivityAction.Conflict, RelativePath.Parse("plan (Konflikt).docx"), false, ActivityResult.Done)));
        await shell.Panel.RefreshAsync(CancellationToken.None);

        Assert.Equal(["carla shared Projekte", "Conflict copy: plan (Konflikt).docx"], _ui.Toasts);

        shell.ApplySettings(new ClientSettings { ShowDesktopNotifications = false });
        _daemon.Notifications.Add(new("n3", "c1", "NAS", "shared", DateTimeOffset.UtcNow, "dora", "Fotos", string.Empty, false));
        await shell.Panel.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, _ui.Toasts.Count);
    }

    [Fact]
    public async Task Newly_synced_files_raise_one_desktop_notification_per_refresh()
    {
        var shell = await ConnectedShellAsync();
        shell.ApplySettings(new ClientSettings());
        await shell.Panel.RefreshAsync(CancellationToken.None); // Bestand beim Start: nicht melden

        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        _daemon.Activity.Add(new("t1", new(now, ActivityAction.Download, RelativePath.Parse("Bericht.pdf"), false, ActivityResult.Done)));
        await shell.Panel.RefreshAsync(CancellationToken.None);
        Assert.Equal(["Bericht.pdf: Downloaded"], _ui.Toasts);

        _daemon.Activity.Add(new("t1", new(now.AddSeconds(1), ActivityAction.Upload, RelativePath.Parse("a.txt"), false, ActivityResult.Done)));
        _daemon.Activity.Add(new("t1", new(now.AddSeconds(2), ActivityAction.Download, RelativePath.Parse("b.txt"), false, ActivityResult.Done)));
        _daemon.Activity.Add(new("t1", new(now.AddSeconds(3), ActivityAction.Upload, RelativePath.Parse("Ordner"), true, ActivityResult.Done)));
        _daemon.Activity.Add(new("t1", new(now.AddSeconds(4), ActivityAction.Upload, RelativePath.Parse("~$x.docx"), false, ActivityResult.Unsynced)));
        await shell.Panel.RefreshAsync(CancellationToken.None);

        Assert.Equal(["Bericht.pdf: Downloaded", "2 files synced"], _ui.Toasts); // Ordner und nicht Übertragenes zählen nicht
    }

    [Fact]
    public void Changed_settings_are_announced_for_the_tray_icon()
    {
        var shell = new ShellViewModel(_ => System.Threading.Tasks.Task.FromResult<IDaemonClient>(_daemon), () => { }, _ui, Texts.English, "logs");
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.ApplySettings(new ClientSettings { Language = "en", MinimalTrayIcon = false });

        Assert.Contains(nameof(ShellViewModel.Settings), changed);
        Assert.False(shell.Settings.MinimalTrayIcon);
    }

    [Fact]
    public async Task Switching_the_language_changes_every_text_at_once()
    {
        _daemon.Tasks.Add(Task("t1"));
        var shell = await ConnectedShellAsync();
        var main = new MainWindowViewModel(shell, _ui);
        await main.RefreshAsync();
        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        main.Settings.LanguageChoice = main.Settings.Languages.Single(l => l.Key == "de");
        await EventuallyAsync(() => main.SyncTasks.Groups.Count == 1 && main.SyncTasks.Groups[0].Tasks[0].ModeText == "(Beidseitig)");

        Assert.Equal(("Sync-Aufgaben", "Anhalten", "Zuletzt geändert"), (main.Texts.NavSyncTasks, shell.PauseMenuText, shell.Panel.Texts.TabRecentlyChanged));
        Assert.Contains(string.Empty, changed);
        Assert.Equal("de", _daemon.SavedSettings[^1].Language);
    }

    [Fact]
    public async Task The_proxy_password_is_sent_separately_and_usage_shows_sizes()
    {
        _daemon.Tasks.AddRange([Task("t1"), Task("t2", local: @"C:\Sync\Fotos")]);
        var shell = await ConnectedShellAsync();
        var settings = new MainWindowViewModel(shell, _ui).Settings;
        await settings.LoadAsync();
        await settings.LoadUsageAsync();

        settings.ProxyPassword = "geheim";
        Assert.Empty(_daemon.SavedSettings); // Tippen speichert nichts
        await settings.SaveProxyPasswordCommand.ExecuteAsync(null);

        Assert.Equal(["geheim"], _daemon.ProxyPasswords);
        Assert.Equal(string.Empty, settings.ProxyPassword);
        Assert.Equal(["1.5 GB used", "3.0 GB used"], settings.Usage.SelectMany(g => g.Tasks).Select(t => t.Detail?.Replace(',', '.')));
        Assert.Equal(("512 B", "1.0 KB", "40 GB"), (Sizes.Format(512), Sizes.Format(1024).Replace(',', '.'), Sizes.Format(40L * 1024 * 1024 * 1024)));
    }

    [Fact]
    public async Task Keep_and_free_up_run_quietly_because_the_explorer_shows_the_result()
    {
        await new ShellCommand(_daemon, _ui, En).RunAsync("keep", @"C:\Sync\Drive\Fotos", CancellationToken.None);
        await new ShellCommand(_daemon, _ui, En).RunAsync("freeup", @"C:\Sync\Drive\a.txt", CancellationToken.None);

        Assert.Equal([@"keep=True C:\Sync\Drive\Fotos", @"keep=False C:\Sync\Drive\a.txt"], _daemon.Calls);
        Assert.Empty(_ui.Messages);
    }

    [Fact]
    public async Task Get_link_copies_the_address_and_says_so()
    {
        _daemon.ShellResults.Enqueue(new ShellResult(true, Message: "https://nas:8443/s/tok123"));

        await new ShellCommand(_daemon, _ui, En).RunAsync("getlink", @"C:\Sync\Drive\a.txt", CancellationToken.None);

        Assert.Equal([@"link C:\Sync\Drive\a.txt"], _daemon.Calls);
        Assert.Equal(["https://nas:8443/s/tok123"], _ui.Copied);
        Assert.Equal("The link was copied to the clipboard:\nhttps://nas:8443/s/tok123", Assert.Single(_ui.Messages));
    }

    [Theory]
    [InlineData("getlink", "link_needs_web", "The server requires a password or an expiry date for links. Please create the link in the web interface (Share → Public link).")]
    [InlineData("getlink", "forbidden", "You are not allowed to do this.")]
    [InlineData("keep", "not_on_demand", "Only available in sync tasks with files on demand.")]
    [InlineData("stopsync", "not_possible", "The sync folder itself can only be removed as a whole task.")]
    [InlineData("resumesync", "forbidden", "You are not allowed to do this.")]
    public async Task Explorer_actions_explain_their_result(string verb, string? error, string message)
    {
        if (error is not null)
        {
            _daemon.ShellResults.Enqueue(new ShellResult(false, error));
        }

        await new ShellCommand(_daemon, _ui, En).RunAsync(verb, @"C:\Sync\Drive\a.txt", CancellationToken.None);

        Assert.Equal(message, Assert.Single(_ui.Messages));
    }

    [Fact]
    public async Task Outside_a_sync_folder_the_menu_only_explains()
    {
        _daemon.PathInfo = null;

        await new ShellCommand(_daemon, _ui, En).RunAsync("versions", @"C:\Anderes\a.txt", CancellationToken.None);

        Assert.Equal(En.NotInSyncFolder, Assert.Single(_ui.Messages));
        Assert.Empty(_daemon.Calls);
    }

    [Fact]
    public async Task Previous_versions_can_be_saved_under_a_new_name_or_restored()
    {
        var now = DateTimeOffset.UtcNow;
        _daemon.Versions.AddRange([new(12, 2048, now, now, "anna", true), new(9, 1024, now.AddDays(-1), now.AddDays(-1), "ben", false)]);
        VersionsViewModel? shown = null;
        _ui.Versions = v =>
        {
            shown = v;
            return System.Threading.Tasks.Task.CompletedTask;
        };
        _ui.SaveFileAnswer = @"C:\Ziel\bericht (alt).txt";

        await new ShellCommand(_daemon, _ui, En).RunAsync("versions", @"C:\Sync\Drive\bericht.txt", CancellationToken.None);

        Assert.NotNull(shown);
        Assert.Equal(["2.0 KB", "1.0 KB"], shown.Rows.Select(r => r.Size.Replace(',', '.')));
        Assert.Equal(9, shown.Selected!.Version.Revision); // die neueste nicht aktuelle ist vorgewählt
        await shown.SaveAsCommand.ExecuteAsync(null);
        await shown.RestoreCommand.ExecuteAsync(null);

        Assert.Contains(_ui.Actions, a => a.StartsWith("save-dialog bericht (", StringComparison.Ordinal));
        Assert.Equal([@"save 9 C:\Ziel\bericht (alt).txt", "restore 9"], _daemon.Calls);
        Assert.Equal([En.VersionSaved, En.ConfirmRestore, En.VersionRestored], _ui.Messages);
    }

    [Fact]
    public async Task The_server_folder_tree_loads_level_by_level()
    {
        _daemon.Folders[":"] = [new("s1", string.Empty, "My Drive", true, true)];
        _daemon.Folders["s1:"] = [new("s1", "Projekte", "Projekte", false, true)];
        _daemon.Folders["s1:Projekte"] = [new("s1", "Projekte/2026", "2026", false, true)];
        var picker = new ServerFolderPickerViewModel(_daemon, "c1", En);
        await picker.LoadAsync(CancellationToken.None);

        var root = Assert.Single(picker.Roots);
        Assert.Equal("…", Assert.Single(root.Children).Name); // noch nicht geladen
        await root.LoadChildrenAsync();
        await root.Children[0].LoadChildrenAsync();
        picker.Selected = root.Children[0].Children[0];

        Assert.Equal(("s1", "Projekte/2026", "My Drive/Projekte/2026"), picker.Result);
    }
}
