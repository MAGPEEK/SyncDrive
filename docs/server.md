# SyncDrive Server

The server is a single ASP.NET Core process in a Linux container. It stores metadata in an embedded SQLite database and file content
as content-addressed blocks on disk, and serves the REST API under `/api/v1` and the web interface under `/` — all on one port.

Every release publishes a multi-arch image (`linux/amd64`, `linux/arm64`) on Docker Hub:

| Image | Tags |
|---|---|
| [`magpeek/syncdrive-server`](https://hub.docker.com/r/magpeek/syncdrive-server) | `latest` (newest release), `<version>` (e.g. `0.18.5`) |

| Port in the container | Protocol | When |
|---|---|---|
| **8080** | HTTP | always (also used by the health check) |
| **8443** | HTTPS | only when a certificate is configured, see [HTTPS](#https) |

- [Requirements](#requirements)
- [Docker Compose](#docker-compose)
- [docker run](#docker-run)
- [HTTPS](#https)
- [Configuration variables](#configuration-variables)
- [Backup and restore](#backup-and-restore)
- [Updating](#updating)
- [Health and version](#health-and-version)
- [Troubleshooting](#troubleshooting)

## Requirements

- Linux host (x64 or arm64) with Docker 24+ and the Compose plugin (`docker compose version`). A NAS with Docker works as well.
- A **local** volume for `/data` (ext4, btrfs, xfs …). SQLite in WAL mode must not live on NFS or SMB shares.

## Docker Compose

The folder **[`docker/`](../docker)** contains a ready-to-run example. Copy `docker-compose.yml` and `.env.example` to the
server, e.g. to `/opt/syncdrive`:

```yaml
services:
  syncdrive:
    image: magpeek/syncdrive-server:${SYNCDRIVE_VERSION:-latest}
    container_name: syncdrive
    restart: unless-stopped
    ports:
      - "${SYNCDRIVE_PORT:-8080}:8080"          # the application listens on 8080 (HTTP) inside the container
    environment:
      TZ: ${TZ:-Europe/Berlin}
      SYNCDRIVE_Server__AdminUserName: ${SYNCDRIVE_ADMIN_USER:-admin}
      SYNCDRIVE_Server__AdminPassword: ${SYNCDRIVE_ADMIN_PASSWORD:?set SYNCDRIVE_ADMIN_PASSWORD in .env}
      SYNCDRIVE_Server__RequireTls: "false"     # plain HTTP; HTTPS: see docs/server.md
      SYNCDRIVE_Server__BackupDirectory: /backup
      SYNCDRIVE_Server__BackupTime: ${SYNCDRIVE_BACKUP_TIME:-02:30}
    volumes:
      - syncdrive-data:/data
      - syncdrive-backup:/backup
    read_only: true
    tmpfs:
      - /tmp
    security_opt:
      - no-new-privileges:true
    cap_drop: [ALL]

volumes:
  syncdrive-data:
  syncdrive-backup:
```

Variables in `.env`:

| Variable | Default | Meaning |
|---|---|---|
| `SYNCDRIVE_PORT` | `8080` | Port on the host |
| `SYNCDRIVE_ADMIN_USER` | `admin` | Administrator created on the first start |
| `SYNCDRIVE_ADMIN_PASSWORD` | — (required) | Its password; only read on the first start |
| `TZ` | `Europe/Berlin` | Time zone (backup time, log timestamps) |
| `SYNCDRIVE_BACKUP_TIME` | `02:30` | Time of the nightly backup (`HH:mm`, in `TZ`) |
| `SYNCDRIVE_VERSION` | `latest` | Image tag; pin a version for controlled updates |

Start:

```bash
cd /opt/syncdrive
cp .env.example .env
nano .env                       # set SYNCDRIVE_ADMIN_PASSWORD
docker compose up -d
docker compose ps               # STATUS shows "healthy" after a few seconds
```

Open `http://<server>:8080/` and sign in as `admin`. The administrator is created on the first start only (when no user exists
yet); afterwards you can remove the password from `.env` and change it in the web interface. Further users, groups and team folders
are created in the account menu → *Administration*.

In the Windows client enter the address with scheme and port, e.g. `http://drive.example.lan:8080` (without a scheme the client assumes
HTTPS).

## docker run

The same without Compose:

```bash
docker run -d --name syncdrive --restart unless-stopped \
  -p 8080:8080 \
  -v syncdrive-data:/data \
  -v syncdrive-backup:/backup \
  -e TZ=Europe/Berlin \
  -e SYNCDRIVE_Server__RequireTls=false \
  -e SYNCDRIVE_Server__AdminUserName=admin \
  -e SYNCDRIVE_Server__AdminPassword='a-long-admin-password' \
  -e SYNCDRIVE_Server__BackupDirectory=/backup \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  magpeek/syncdrive-server:latest
```

## HTTPS

Plain HTTP transmits passwords and files unencrypted. It is acceptable on a trusted home network; anywhere else use one of the
following.

### Certificate directly in the container

The server can serve HTTPS itself on port 8443 — no proxy needed. Put the certificate and key (PEM) in a folder, readable for the
server user (UID 1654):

```bash
sudo mkdir -p /opt/syncdrive/certs
sudo cp fullchain.pem /opt/syncdrive/certs/cert.pem
sudo cp privkey.pem   /opt/syncdrive/certs/key.pem
sudo chown -R 1654 /opt/syncdrive/certs && sudo chmod 400 /opt/syncdrive/certs/key.pem
```

No certificate yet? A self-signed one for a first test:
`openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj /CN=drive.example.lan -keyout key.pem -out cert.pem`.
The Windows client shows its fingerprint on first connect and asks you to trust it; browsers warn once.

Change the service in `docker-compose.yml`:

```yaml
    ports:
      - "8443:8443"                             # HTTPS only; 8080 stays internal for the health check
    environment:
      # … as before, but:
      SYNCDRIVE_Server__RequireTls: "true"
      ASPNETCORE_HTTPS_PORTS: "8443"
      ASPNETCORE_Kestrel__Certificates__Default__Path: /certs/cert.pem
      ASPNETCORE_Kestrel__Certificates__Default__KeyPath: /certs/key.pem
    volumes:
      # … as before, plus:
      - ./certs:/certs:ro
```

Then `docker compose up -d` and open `https://drive.example.lan:8443/`. Clients use port 8443 by default, so `drive.example.lan`
is enough as address in the client. The certificate is read at start: after renewing it run `docker compose restart`.

### Behind a reverse proxy

If a reverse proxy (nginx, Traefik, Caddy, a NAS proxy) already terminates TLS, bind the port to localhost
(`127.0.0.1:8080:8080`) and set:

```yaml
      SYNCDRIVE_Server__RequireTls: "true"
      SYNCDRIVE_Server__TrustForwardedHeaders: "true"   # only when nothing but the proxy can reach port 8080
```

Requirements for the proxy:

- forward `X-Forwarded-Proto` (and `X-Forwarded-For`);
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

## Configuration variables

All settings are environment variables with the prefix `SYNCDRIVE_Server__` (two underscores). They can also be set in
`appsettings.json` under the section `Server`.

| Variable | Default | Description |
|---|---|---|
| `SYNCDRIVE_Server__DataDirectory` | `/data` (image) | Directory for `server.db`, `blocks/`, `thumbnails/` and the sign-in page assets. Must be a local volume. |
| `SYNCDRIVE_Server__RequireTls` | `true` | Reject requests that did not arrive via HTTPS (`/healthz` is exempt). Set `false` for plain HTTP. |
| `SYNCDRIVE_Server__TrustForwardedHeaders` | `false` | Honour `X-Forwarded-Proto` and `X-Forwarded-For` from any sender. Only enable behind a proxy that alone can reach the port; also needed so the brute-force limiter sees real client addresses. |
| `SYNCDRIVE_Server__AdminUserName` | — | Creates this administrator on the first start (only if no user exists yet). |
| `SYNCDRIVE_Server__AdminPassword` | — | Password for that administrator. |
| `SYNCDRIVE_Server__AdminPasswordFile` | — | Alternative: file containing the password (Docker or Kubernetes secret; must be readable for UID 1654). |
| `SYNCDRIVE_Server__BackupDirectory` | — (off) | Target of the nightly backup, ideally another disk. Empty disables the backup. |
| `SYNCDRIVE_Server__BackupTime` | `02:30` | Time of day for the backup in the container time zone (`TZ`). |
| `SYNCDRIVE_Server__BackupsToKeep` | `7` | Number of database snapshots to keep; blocks are only ever added. |
| `SYNCDRIVE_Server__MaxBlockBytes` | `8388608` (8 MiB) | Largest accepted block upload. Clients send at most 4 MiB. |
| `SYNCDRIVE_Server__MaxLongPollSeconds` | `60` | Longest wait of a change long-poll request. Keep it below a proxy's read timeout. |
| `SYNCDRIVE_Server__MaxPublicUploadBytes` | `4294967296` (4 GiB) | Largest file anyone may upload through a file request link. |
| `SYNCDRIVE_Server__Argon2MemoryKiB` | `65536` | Argon2id memory cost for password hashes. |
| `SYNCDRIVE_Server__Argon2Iterations` | `3` | Argon2id time cost. |
| `SYNCDRIVE_Server__Argon2Parallelism` | `4` | Argon2id lanes. The parameters are stored with each hash, so changing them later is safe. |

Variables of the ASP.NET base image:

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `8080` | HTTP port inside the container (the health check uses 8080) |
| `ASPNETCORE_HTTPS_PORTS` | — | HTTPS port inside the container, together with a certificate |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` | — | Certificate (PEM or PFX) |
| `ASPNETCORE_Kestrel__Certificates__Default__KeyPath` | — | Private key (PEM) |
| `ASPNETCORE_Kestrel__Certificates__Default__Password` | — | Password of a PFX or encrypted key |
| `TZ` | `UTC` | Time zone |
| `DOTNET_gcServer` | `0` | Workstation GC keeps memory use low on small machines |

Volumes: `/data` (required) and `/backup` (optional). The process runs as the unprivileged user `app` (UID 1654); a bind-mounted
directory must be writable for that UID (`sudo chown 1654 /path/to/dir`). To put the backup on another disk, replace the volume,
e.g. `- /mnt/usb/syncdrive-backup:/backup`.

Settings that live in the database and are changed in the web interface (account menu → *Administration*): users, quotas,
groups, team folders, sharing policies (public links, file requests, password requirement, maximum validity) and the sign-in page
(server name, welcome text, background image).

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

Docker Compose:

```bash
cd /opt/syncdrive
docker compose pull        # with SYNCDRIVE_VERSION=latest, or set the new version in .env first
docker compose up -d
docker image prune -f      # optional: remove the old image
```

`docker run`: `docker pull magpeek/syncdrive-server:latest`, then `docker rm -f syncdrive` and start it again with the same
command; the data stays in the volumes.

Database migrations run automatically on start. Clients and server of the same minor version always work together; before 1.0.0 a
new minor version may require updating the clients as well (see [CHANGELOG.md](../CHANGELOG.md)).

## Health and version

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | `200` when the server is up (used by the Docker health check, works without TLS) |
| `GET /api/v1/info` | Product name and version, e.g. `{"product":"SyncDrive","version":"0.18.5"}` |

```bash
curl http://localhost:8080/api/v1/info
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Container stays `unhealthy` / restarts | `docker compose logs syncdrive`. Most often `/data` or `/backup` is a bind mount not writable for UID 1654, or the certificate is not readable for UID 1654. |
| `docker compose up` says `SYNCDRIVE_ADMIN_PASSWORD` is missing | Create `.env` from `.env.example` and set the password. |
| Sign-in as `admin` fails | The administrator is only created when the database has no users yet; changing the password in `.env` later has no effect. Another administrator can set a new password (*Administration → Users*); on a fresh installation run `docker compose down -v` and start again. |
| Every request returns `400 invalid_request` (TLS) | `RequireTls` is `true`, but the request came via plain HTTP. Use HTTPS, set `RequireTls=false`, or behind a proxy set `TrustForwardedHeaders=true` and forward `X-Forwarded-Proto`. |
| The client cannot connect to an HTTP server | Enter the address with `http://` and the port; without a scheme the client uses HTTPS on port 8443. |
| Clients disconnect every minute | A proxy buffers or times out long-poll requests: disable buffering, read timeout above 60 s. |
