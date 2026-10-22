# SyncDrive Server

The server is a single ASP.NET Core process in a Linux container. It stores metadata in an embedded SQLite database and file content
as content-addressed blocks on disk, serves the REST API under `/api/v1` and the web interface under `/`.

- [Requirements](#requirements)
- [Docker Compose (recommended)](#docker-compose-recommended)
- [Plain `docker run`](#plain-docker-run)
- [Configuration variables](#configuration-variables)
- [Reverse proxy and TLS](#reverse-proxy-and-tls)
- [Backup and restore](#backup-and-restore)
- [Updating](#updating)
- [Health and version](#health-and-version)

## Requirements

- Linux host (x64 or arm64) with Docker 24+ and the Compose plugin — a NAS with Docker works as well.
- A **local** volume for `/data` (ext4, btrfs, xfs …). SQLite in WAL mode must not live on NFS or SMB shares.

## Docker Compose (recommended)

`deploy/docker-compose.yml` starts two containers:

| Service | Image | Purpose |
|---|---|---|
| `server` | built from `Dockerfile` | SyncDrive server, reachable only on the internal network |
| `proxy` | `caddy:2` | TLS termination on port **8443**, forwards to `server:8080` |

```bash
git clone https://github.com/MAGPEEK/SyncDrive.git
cd SyncDrive/deploy
printf '%s' 'a-long-admin-password' > admin-password.txt
chmod 600 admin-password.txt
SYNCDRIVE_HOST=drive.example.lan TZ=Europe/Berlin docker compose up -d --build
docker compose ps        # both services "healthy" / "running"
```

The administrator account `admin` is created on the first start only (when there are no users yet). The password is passed as a
Docker secret; you can delete `admin-password.txt` after the first start and change the password later in the web interface.

Variables read by the Compose file (put them in `deploy/.env` or export them):

| Variable | Default | Meaning |
|---|---|---|
| `SYNCDRIVE_HOST` | `localhost` | Host name clients use; Caddy issues the certificate for it |
| `TZ` | `Europe/Berlin` | Time zone of the server (backup time, log timestamps) |
| `SYNCDRIVE_BACKUP_TIME` | `02:30` | Time of the nightly backup (`HH:mm`, in `TZ`) |

The container is hardened: read-only image file system, all Linux capabilities dropped, `no-new-privileges`, limits for memory,
CPU and processes, rotating JSON logs.

## Plain `docker run`

Without the proxy (for example behind an existing reverse proxy that already terminates TLS). Every release also publishes a
prebuilt multi-arch image (`linux/amd64`, `linux/arm64`) on Docker Hub as `magpeek/syncdrive-server` with the tags `<version>` and `latest`; use it instead of building:

```bash
docker build -t syncdrive-server .     # or: docker pull magpeek/syncdrive-server:latest

docker run -d --name syncdrive \
  --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -v syncdrive-data:/data \
  -v syncdrive-backup:/backup \
  -e TZ=Europe/Berlin \
  -e SYNCDRIVE_Server__AdminUserName=admin \
  -e SYNCDRIVE_Server__AdminPasswordFile=/run/secrets/admin_password \
  -v "$PWD/admin-password.txt:/run/secrets/admin_password:ro" \
  -e SYNCDRIVE_Server__TrustForwardedHeaders=true \
  -e SYNCDRIVE_Server__BackupDirectory=/backup \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  syncdrive-server
```

For a quick local test without TLS add `-e SYNCDRIVE_Server__RequireTls=false` and open `http://localhost:8080/`.
Never run without TLS on a network you do not control.

## Configuration variables

All settings are environment variables with the prefix `SYNCDRIVE_Server__` (two underscores). They can also be set in
`appsettings.json` under the section `Server`.

| Variable | Default | Description |
|---|---|---|
| `SYNCDRIVE_Server__DataDirectory` | `/data` (image) | Directory for `server.db`, `blocks/`, `thumbnails/` and the sign-in page assets. Must be a local volume. |
| `SYNCDRIVE_Server__RequireTls` | `true` | Reject requests that did not arrive via HTTPS (`/healthz` is exempt). Behind a proxy combine with `TrustForwardedHeaders`. |
| `SYNCDRIVE_Server__TrustForwardedHeaders` | `false` | Honour `X-Forwarded-Proto` and `X-Forwarded-For` from any sender. Only enable when the port is reachable by the proxy alone; also needed so the brute-force limiter sees real client addresses. |
| `SYNCDRIVE_Server__AdminUserName` | — | Creates this administrator on the first start (only if no user exists yet). |
| `SYNCDRIVE_Server__AdminPassword` | — | Password for that administrator. Prefer `AdminPasswordFile`. |
| `SYNCDRIVE_Server__AdminPasswordFile` | — | File containing the administrator password (Docker or Kubernetes secret). |
| `SYNCDRIVE_Server__BackupDirectory` | — (off) | Target of the nightly backup, ideally another disk. Empty disables the backup. |
| `SYNCDRIVE_Server__BackupTime` | `02:30` | Time of day for the backup in the container time zone (`TZ`). |
| `SYNCDRIVE_Server__BackupsToKeep` | `7` | Number of database snapshots to keep; blocks are only ever added. |
| `SYNCDRIVE_Server__MaxBlockBytes` | `8388608` (8 MiB) | Largest accepted block upload. Clients send at most 4 MiB. |
| `SYNCDRIVE_Server__MaxLongPollSeconds` | `60` | Longest wait of a change long-poll request. Keep it below your proxy's read timeout. |
| `SYNCDRIVE_Server__MaxPublicUploadBytes` | `4294967296` (4 GiB) | Largest file anyone may upload through a file request link. |
| `SYNCDRIVE_Server__Argon2MemoryKiB` | `65536` | Argon2id memory cost for password hashes. |
| `SYNCDRIVE_Server__Argon2Iterations` | `3` | Argon2id time cost. |
| `SYNCDRIVE_Server__Argon2Parallelism` | `4` | Argon2id lanes. The parameters are stored with each hash, so changing them later is safe. |

Variables of the base image that you may want to change:

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `8080` | HTTP port inside the container |
| `TZ` | `UTC` | Time zone |
| `DOTNET_gcServer` | `0` | Workstation GC keeps memory use low on small machines |

Volumes: `/data` (required) and `/backup` (optional). The process runs as the unprivileged user `app` (UID 1654); a bind-mounted
directory must be writable for that UID (`sudo chown 1654 /path/to/dir`).

Settings that live in the database and are changed in the web interface (account menu → *Administration*): users, quotas,
groups, team folders, sharing policies (public links, file requests, password requirement, maximum validity) and the sign-in page
(server name, welcome text, background image).

## Reverse proxy and TLS

The server speaks plain HTTP on port 8080 and expects a TLS-terminating proxy in front of it (or `RequireTls=false` for tests only).
Requirements for any proxy:

- forward `X-Forwarded-Proto` (and `X-Forwarded-For`) and set `TrustForwardedHeaders=true` on the server;
- **do not buffer responses** — clients hold long-poll requests open for up to `MaxLongPollSeconds`;
- allow request bodies of at least `MaxBlockBytes` (8 MiB);
- read timeout above `MaxLongPollSeconds` (e.g. 90 s).

nginx example:

```nginx
server {
    listen 8443 ssl;
    server_name drive.example.lan;
    ssl_certificate     /etc/ssl/drive.crt;
    ssl_certificate_key /etc/ssl/drive.key;

    client_max_body_size 16m;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_buffering off;
        proxy_read_timeout 90s;
    }
}
```

Clients default to port **8443** when the address has no port.

## Backup and restore

With `BackupDirectory` set, the server writes a consistent snapshot every night:

```
/backup/
  db/server-20261022-003000.db   # database snapshots (VACUUM INTO, UTC time), the newest BackupsToKeep are kept
  blocks/ab/cd/<hash>             # file content, incremental — only new blocks are copied
  portal/                         # sign-in page settings and background
```

Restore: stop the server, copy the newest `db/server-*.db` to `/data/server.db`, copy `blocks/` to `/data/blocks/`
(and `portal/` to `/data/portal/`), start the server. Thumbnails are a cache and are rebuilt on demand.

## Updating

```bash
cd SyncDrive && git pull
cd deploy && docker compose up -d --build
```

Database migrations run automatically on start. Clients and server of the same minor version always work together; before 1.0.0 a
new minor version may require updating the clients as well (see [CHANGELOG.md](../CHANGELOG.md)).

## Health and version

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | `200` when the server is up (used by the Docker health check, works without TLS) |
| `GET /api/v1/info` | Product name and version, e.g. `{"product":"SyncDrive","version":"0.18.2"}` |
