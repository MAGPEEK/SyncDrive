<p align="center">
  <img src="src/SyncDrive.Web/wwwroot/favicon.svg" width="96" alt="SyncDrive logo">
</p>

<h1 align="center">SyncDrive</h1>

<p align="center">
  Self-hosted file sync and share — your files on your own server, in sync on every device.
</p>

<p align="center">
  <a href="https://hub.docker.com/r/magpeek/syncdrive-server"><img src="https://img.shields.io/docker/pulls/magpeek/syncdrive-server?label=docker%20pulls" alt="Docker pulls"></a>
  <a href="https://hub.docker.com/r/magpeek/syncdrive-server/tags"><img src="https://img.shields.io/docker/v/magpeek/syncdrive-server?sort=semver&label=image" alt="Docker image"></a>
  <a href="https://github.com/MAGPEEK/SyncDrive/releases"><img src="https://img.shields.io/github/v/release/MAGPEEK/SyncDrive?include_prereleases&label=client" alt="Client release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a>
</p>

# What is SyncDrive?

SyncDrive keeps your files on your own server — a NAS, a mini PC or a small VPS — and in sync on your Windows PCs. Share folders
and links, restore earlier versions, and reach everything from the browser.

- **Sync** folders two-way, upload-only or download-only; keep files **online-only** and fetch them when opened.
- **Web interface** to browse, upload, download, search, preview images and restore from the recycle bin or versions.
- **Share** with other users, groups and team folders, via public links with password and expiry, or collect files with file requests.
- **Administer** users, quotas and sharing policies in the browser.

<p align="center">
  <img src="docs/images/web-files.png" width="860" alt="SyncDrive web interface">
</p>

# Requirements

- A Linux host (x64 or arm64) with Docker and the Compose plugin — a NAS with Docker works as well
- A local disk for the data volume (not an NFS/SMB share)

# Usage

The server is a single container, [`magpeek/syncdrive-server`](https://hub.docker.com/r/magpeek/syncdrive-server). It listens on
port **8080** and serves the web interface and the API for the clients.

## Docker Compose

Copy [`docker/docker-compose.yml`](docker/docker-compose.yml) to your server (or paste it into Portainer / your NAS as a stack),
replace `change-me-to-a-long-password` with your administrator password, then:

```bash
docker compose up -d
```

## docker run

```bash
docker run -d --name syncdrive --restart unless-stopped \
  -p 8080:8080 \
  -v syncdrive-data:/data \
  -e SYNCDRIVE_Server__RequireTls=false \
  -e SYNCDRIVE_Server__AdminUserName=admin \
  -e SYNCDRIVE_Server__AdminPassword='a-long-admin-password' \
  magpeek/syncdrive-server:latest
```

## Sign in

Open `http://<server>:8080/` and sign in as `admin`. The administrator is created on the first start; further users, groups and
team folders are added in the account menu → *Administration*.

Plain HTTP is fine on a trusted home network. Otherwise enable HTTPS — directly in the container with your certificate, or behind a
reverse proxy: see **[HTTPS](docs/server.md#https)**.

# Configuration

Environment variables of the container:

| Variable | Default | Description |
|---|---|---|
| `SYNCDRIVE_Server__AdminUserName` | — | Administrator created on the first start (only if no user exists yet) |
| `SYNCDRIVE_Server__AdminPassword` | — | Password of that administrator |
| `SYNCDRIVE_Server__AdminPasswordFile` | — | Alternative: file containing the password (Docker/Kubernetes secret) |
| `SYNCDRIVE_Server__RequireTls` | `true` | Reject plain HTTP requests; set `false` for HTTP |
| `SYNCDRIVE_Server__TrustForwardedHeaders` | `false` | Trust `X-Forwarded-*` headers — only behind a reverse proxy |
| `SYNCDRIVE_Server__DesktopClientUrl` | project release page | Download link for the desktop client shown in the web interface |
| `SYNCDRIVE_Server__MaxPublicUploadBytes` | `4294967296` | Largest file upload via file request link (4 GiB) |
| `SYNCDRIVE_Server__MaxLongPollSeconds` | `60` | Longest wait for change notifications; keep below a proxy's timeout |
| `TZ` | `UTC` | Time zone |

Volume: `/data` (required). HTTPS variables, all further settings, updates and troubleshooting:
**[docs/server.md](docs/server.md)**.

# Desktop client

<p>
  <a href="https://github.com/MAGPEEK/SyncDrive/releases"><b>⬇ Download SyncDrive Client for Windows</b></a>
  — Windows 10 (2004+) and 11, x64
</p>

| File | Use it for |
|---|---|
| `SyncDrive-Client-<version>-<build>-x64.exe` | Setup — double-click and follow the wizard |
| `SyncDrive-Client-<version>-<build>-x64.msi` | Silent install and software distribution (Intune, GPO) |

No administrator rights needed. After installation, enter your server address — e.g. `http://drive.example.lan:8080` — and your
user name. The installer also adds the command line tool `syncdrive_cli`. Details: **[docs/client.md](docs/client.md)**.

# Other deployment methods

- **Kubernetes** — manifests and an Ingress example in [`kubernetes/`](kubernetes)
- **Ansible** — playbook in [`ansible/`](ansible)
- **Build from source** — [docs/development.md](docs/development.md)

# License

[MIT](LICENSE)
