# SyncDrive Client for Windows

The client consists of three programs that are installed together:

| Program | Role |
|---|---|
| `syncdrive.exe` | Tray icon, main window, setup wizard, settings, Explorer context menu actions |
| `syncdrive-daemon.exe` | Background service of the signed-in user: runs the sync tasks, keeps connection data, talks to the UI over a named pipe |
| `syncdrive_cli.exe` | Command line for scripts and power users; uses the same background service |

Requirements: Windows 10 version 2004 or later, or Windows 11; x64. Files on demand need NTFS.

## Downloads

Every [release](https://github.com/MAGPEEK/SyncDrive/releases) contains:

| File | Description |
|---|---|
| `SyncDrive-Client-<version>-<build>-x64.exe` | Setup program with license page and progress; contains the MSI |
| `SyncDrive-Client-<version>-<build>-x64.msi` | Windows Installer package for silent and managed installation |
| `SyncDrive-Client-<version>-<build>-x64.sha256` | SHA-256 checksums |

Verify a download in PowerShell:

```powershell
(Get-FileHash .\SyncDrive-Client-0.18.1-1-x64.msi -Algorithm SHA256).Hash.ToLower()
```

The packages are not code-signed yet. Windows SmartScreen may warn on first start; choose *More info → Run anyway* after checking
the checksum.

## Installation

Double-click the `.exe` (or the `.msi`). The client is installed **for the current user** into `%LOCALAPPDATA%\Programs\SyncDrive` —
no administrator rights, no UAC prompt. The installer

- copies the programs together with their own .NET runtime (nothing else to install),
- adds a *SyncDrive* entry to the Start menu,
- adds the installation folder to the user `PATH` (so `syncdrive_cli` works in new terminals),
- starts SyncDrive at the end, which opens the setup wizard.

Updates are installed the same way: run the newer package; running programs are closed and restarted, settings and sync tasks stay.

### Silent installation

```powershell
msiexec /i SyncDrive-Client-<version>-<build>-x64.msi /qn LAUNCHAPP=0 /l*v install.log
```

| MSI property | Default | Meaning |
|---|---|---|
| `LAUNCHAPP` | `1` | Start SyncDrive after installation. `0` for deployment scripts. |

The setup `.exe` accepts the usual switches as well: `/quiet`, `/passive`, `/log <file>`, `/uninstall`.

## First start

1. Enter the server address — `drive.example.lan`, `drive.example.lan:9443` or `https://drive.example.lan:8443/`.
   Without a port the client uses **8443**, without a scheme HTTPS. For a server on plain HTTP enter e.g. `http://drive.example.lan:8080`.
2. Enter user name and password. If the server uses a self-signed certificate, the client shows its SHA-256 fingerprint; compare it
   with the server and confirm. The client then pins this certificate.
3. Choose a folder on the server and a local folder, the sync direction (two-way, upload only, download only) and whether files
   should be downloaded on demand.

The password is not stored: the client registers itself as a device and keeps only a device token, protected with Windows DPAPI.
You can revoke the device at any time in the web interface (account menu → *Client list* → *Unlink*).

## Explorer menu

Right-click a file or folder inside a sync folder → **SyncDrive**:

| Entry | For | What it does |
|---|---|---|
| Get link | files, folders | Copy the public link to the clipboard (creates one without password if there is none; needs the right to share) |
| Browse previous versions | files | List, save or restore earlier versions |
| Always keep on this device | files, folders (files on demand only) | Download now and keep the content locally |
| Free up space | files, folders (files on demand only) | Remove the local content; the file stays visible and is fetched again when opened |
| Stop syncing this folder / Sync this folder again | folders | Exclude a subfolder from syncing or include it again |

## Where the client keeps its data

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\Programs\SyncDrive` | Programs (removed on uninstall) |
| `%LOCALAPPDATA%\SyncDrive` | Configuration, sync state, device tokens (DPAPI), logs — kept on uninstall |
| `%LOCALAPPDATA%\SyncDrive\logs` | `daemon.log` for troubleshooting |

Set the environment variable `SYNCDRIVE_CLIENT_HOME` to move the data folder (e.g. for portable or test setups).

## Command line

`syncdrive_cli` controls the background service and starts it if necessary. Output is English and tab-separated, so it can be parsed
by scripts.

```text
Usage: syncdrive_cli <command> [options]

  connect <server> -u <user> [--password-stdin] [--trust <sha256>]
  connections
  ls [<space-id> [<path>]] [-c <connection>]
  sync <local-folder> -s <space-id> [-p <server-path>] [-c <connection>]
       [--direction two-way|upload|download] [--on-demand]
  desync <task-id|local-folder>
  tasks
  status
  pause [<task-id>]
  resume [<task-id>]
  version
```

Examples:

```powershell
# sign in non-interactively (password from a secret store)
Get-Secret SyncDrive -AsPlainText | syncdrive_cli connect drive.example.lan -u alice --password-stdin

# self-signed certificate: the first attempt prints the fingerprint, repeat with --trust after checking it
syncdrive_cli connect drive.example.lan -u alice --trust 3f:a1:...:9c

# back up a folder to the server (upload only)
syncdrive_cli sync D:\Photos -s sp_1234 -p Backup/Photos --direction upload

# wait until everything is synced
while ((syncdrive_cli status) -match 'Syncing') { Start-Sleep 5 }
```

`-c` selects a connection when there is more than one; it accepts the connection id, its name or the server address.

| Exit code | Meaning |
|---|---|
| `0` | Success (for `status`: no task in error) |
| `1` | The operation failed, or `status` found a task in error |
| `2` | Wrong usage (unknown command, missing argument) |

## Uninstalling

*Settings → Apps → Installed apps → SyncDrive Client → Uninstall*, or `msiexec /x SyncDrive-Client-<version>-<build>-x64.msi /qn`.
The uninstaller closes SyncDrive and removes the programs, the Start menu entry, the `PATH` entry, the autostart entry and the
Explorer context menu. Your synced files and `%LOCALAPPDATA%\SyncDrive` stay; delete that folder to remove all settings.
Online-only placeholders that were never downloaded cannot be opened without the client — make them available offline
(right-click → *SyncDrive* → *Always keep on this device*) before uninstalling if you need them.
