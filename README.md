<p align="center">
  <img src="src/SyncDrive.Web/wwwroot/favicon.svg" width="96" alt="SyncDrive logo">
</p>

<h1 align="center">SyncDrive</h1>

<p align="center">
  Self-hosted file sync and share — your files on your own server, in sync on every device.
</p>

<p align="center">
  <a href="https://github.com/MAGPEEK/SyncDrive/actions/workflows/ci.yml"><img src="https://github.com/MAGPEEK/SyncDrive/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/MAGPEEK/SyncDrive/releases"><img src="https://img.shields.io/github/v/release/MAGPEEK/SyncDrive?include_prereleases&label=release" alt="Release"></a>
  <a href="https://hub.docker.com/r/magpeek/syncdrive-server"><img src="https://img.shields.io/docker/v/magpeek/syncdrive-server?sort=semver&label=docker" alt="Docker Hub"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a>
</p>

---

## Introduction

SyncDrive is an open-source file synchronization and sharing system that you run on your own hardware. A small server in a
Linux container keeps your files; a Windows client keeps local folders in sync with it; a web interface gives you access from
any browser. Your data never leaves your server.

SyncDrive is built for homes and small teams who want the comfort of a cloud drive without handing their files to a cloud
provider: a NAS, a mini PC or a small VPS is all it takes.

With SyncDrive you can

- **sync folders** between your PCs and the server — two-way, upload-only (backup) or download-only, with as many sync tasks as you like;
- **keep files online-only** and download them on demand when you open them, so a laptop can see terabytes without storing them
  (Windows Cloud Files, with status icons and "Always keep on this device" / "Free up space" in Explorer);
- **work from Explorer**: status icons, a SyncDrive context menu, browse and restore previous versions, lock files while you edit them;
- **use the web interface**: browse, upload by drag & drop, download files or whole folders as ZIP, search, recently changed files,
  recycle bin, versions, starred files, labels, notifications and image previews;
- **share** folders with other users (viewer, editor, manager …), create **public links** with password and expiry date, and
  collect files from anyone with **file requests**;
- **work in teams** with groups and team folders that belong to the organization rather than one person;
- **administer** users, storage quotas, groups, team folders, sharing policies and the sign-in page from the browser;
- **sleep well**: every change creates a version, deleted files go to a recycle bin, conflicts are kept side by side, and the server
  backs itself up every night.

## Screenshots

<p align="center">
  <img src="docs/images/web-files.png" width="900" alt="File list with image previews"><br>
  <em>Web interface: files with image previews, categories, labels and the action bar</em>
</p>

<p align="center">
  <img src="docs/images/web-share.png" width="440" alt="Share dialog">
  <img src="docs/images/web-signin.png" width="440" alt="Sign-in page"><br>
  <em>Sharing a folder with roles · sign-in page with your own server name and background</em>
</p>

## Components

| Component | What it is | Runs on |
|---|---|---|
| **SyncDrive Server** | REST API, sync engine, block store (SQLite + files), web interface, daily backup | Linux container (Docker, Podman, any NAS with Docker) |
| **SyncDrive Client** | Tray app, background service and Explorer integration; `syncdrive_cli` for scripts | Windows 10 (2004+) / Windows 11, x64 |
| **Web interface** | Blazor WebAssembly app served by the server under `/` | Any current browser |

## Getting started

### 1. Set up the server with Docker

You need Docker with the Compose plugin on a Linux machine (or a NAS with Docker support). The server image is published on
Docker Hub as **[`magpeek/syncdrive-server`](https://hub.docker.com/r/magpeek/syncdrive-server)** (`linux/amd64`, `linux/arm64`;
tags `latest` and `<version>`).

The application listens on port **8080** inside the container.

**Docker Compose:** copy the example folder **[`docker/`](docker)** (`docker-compose.yml`, `.env.example`) to the server, then:

```bash
cp .env.example .env
nano .env                  # set SYNCDRIVE_ADMIN_PASSWORD (and the port, if 8080 is taken)
docker compose up -d
```

**Or with `docker run`:**

```bash
docker run -d --name syncdrive --restart unless-stopped \
  -p 8080:8080 \
  -v syncdrive-data:/data -v syncdrive-backup:/backup \
  -e SYNCDRIVE_Server__RequireTls=false \
  -e SYNCDRIVE_Server__AdminUserName=admin \
  -e SYNCDRIVE_Server__AdminPassword='a-long-admin-password' \
  -e SYNCDRIVE_Server__BackupDirectory=/backup \
  magpeek/syncdrive-server:latest
```

Open `http://<server>:8080/` and sign in as `admin`. The administrator is created on the first start only.

Plain HTTP is fine on a trusted home network. Anywhere else, use HTTPS: either give the container a certificate (it then serves
HTTPS itself on port 8443) or put it behind your reverse proxy. Both are described, together with all configuration variables,
backup, updates and troubleshooting, in **[docs/server.md](docs/server.md)**.

### 2. Install the Windows client

Download the latest version from the **[Releases page](https://github.com/MAGPEEK/SyncDrive/releases/latest)**:

| Download | Use it for |
|---|---|
| `SyncDrive-Client-<version>-<build>-x64.exe` | Setup program — double-click and follow the wizard (recommended) |
| `SyncDrive-Client-<version>-<build>-x64.msi` | Windows Installer package — for silent installs and software distribution (Intune, GPO, scripts) |
| `SyncDrive-Client-<version>-<build>-x64.sha256` | SHA-256 checksums of both files |

The client installs per user into `%LOCALAPPDATA%\Programs\SyncDrive` and needs **no administrator rights**. It brings its own .NET
runtime. After installation the setup wizard opens: enter the server address, your user name and
password, then choose a server folder and a local folder to sync. Enter the address with scheme and port as the server is
reachable, e.g. `http://drive.example.lan:8080` or `https://drive.example.lan:8443`.

Silent installation:

```powershell
msiexec /i SyncDrive-Client-<version>-<build>-x64.msi /qn LAUNCHAPP=0
```

Details, uninstalling and the command line are in **[docs/client.md](docs/client.md)**.

### 3. Command line: `syncdrive_cli`

The installer puts `syncdrive_cli.exe` on your `PATH`. It talks to the same background service as the tray app:

```text
syncdrive_cli connect drive.example.lan -u alice      # sign in (asks for the password)
syncdrive_cli ls                                       # list your spaces
syncdrive_cli sync D:\Projects -s sp_1234 -p Projects  # sync a server folder to a local folder
syncdrive_cli status                                   # state of all sync tasks
syncdrive_cli pause | resume [task-id]
syncdrive_cli desync D:\Projects                       # stop syncing (local files stay)
```

Exit codes: `0` success, `1` failure, `2` wrong usage — handy for scripts.

## Features in detail

**Sync engine**
- Content-defined chunking (FastCDC, 256 KiB – 4 MiB blocks): only changed blocks travel, identical content is stored once.
- Live updates through long polling; changes on the server reach clients within seconds.
- Rename and move detection, conflict copies that keep both versions, local recycle bin for files deleted by sync.
- Filters for temporary files, maximum file size and path length, per-task folder selection.

**Server**
- Single process with embedded SQLite (WAL) and a content-addressed block store — no separate database or cache container needed.
- Versions (10 per file by default), recycle bin, file locking, favorites, labels, notifications, image thumbnails.
- Sharing with roles (Previewer, Viewer, Editor, Manager, Owner), public links, file requests, groups, team folders, sharing policies.
- Admin console: users, quotas (`507` when exceeded), password resets, device management.
- Nightly consistent backup (`VACUUM INTO` + incremental blocks), health endpoint, hardened container (read-only, no capabilities).

**Security**
- Passwords hashed with Argon2id; per-device tokens stored only as hashes and revocable from the web interface.
- HTTPS directly in the container or behind a reverse proxy; clients can pin self-signed certificates.
- Brute-force protection for sign-in and link passwords (`429` with `Retry-After`).
- Blocks are scoped per space: knowing a hash never gives access to someone else's file.

## Development

You only need this to work on SyncDrive itself; for running it, the Docker image and the release downloads are enough.
Building, tests and the project structure are described in **[docs/development.md](docs/development.md)**.

## Versioning

SyncDrive follows [Semantic Versioning](https://semver.org/). Until 1.0.0 the protocol may still change between minor versions.
See [CHANGELOG.md](CHANGELOG.md) for what changed in each release.

## License

SyncDrive is released under the [MIT License](LICENSE).
