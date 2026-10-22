# SyncDrive Server

The server is a single ASP.NET Core process in a Linux container. It stores metadata in an embedded SQLite database and file content
as content-addressed blocks on disk, serves the REST API under `/api/v1` and the web interface under `/`.

Every release publishes a multi-arch image (`linux/amd64`, `linux/arm64`) on Docker Hub:

| Image | Tags |
|---|---|
| [`magpeek/syncdrive-server`](https://hub.docker.com/r/magpeek/syncdrive-server) | `latest` (newest release), `<version>` (e.g. `0.18.2`) |

- [Requirements](#requirements)
- [Option A: Docker Compose with TLS (recommended)](#option-a-docker-compose-with-tls-recommended)
- [Option B: `docker run` behind your own reverse proxy](#option-b-docker-run-behind-your-own-reverse-proxy)
- [Option C: Build the image from source](#option-c-build-the-image-from-source)
- [Configuration variables](#configuration-variables)
- [Reverse proxy and TLS](#reverse-proxy-and-tls)
- [Backup and restore](#backup-and-restore)
- [Updating](#updating)
- [Health and version](#health-and-version)
- [Troubleshooting](#troubleshooting)

## Requirements

- Linux host (x64 or arm64) with Docker 24+ and the Compose plugin (`docker compose version`). A NAS with Docker works as well.
- A **local** volume for `/data` (ext4, btrfs, xfs …). SQLite in WAL mode must not live on NFS or SMB shares.
- Port **8443** reachable for the clients (Option A), or an existing TLS reverse proxy (Option B).

## Option A: Docker Compose with TLS (recommended)

Two containers: the SyncDrive server, reachable only on an internal Docker network, and [Caddy](https://caddyserver.com/) as
reverse proxy that terminates TLS on port 8443. No source code is needed.

The ready-made files are in the folder **[`docker/`](../docker)** (`docker-compose.yml`, `Caddyfile`, `.env.example` and a short
README): copy the folder to the server and continue with step 4. The steps below show the same files in full.

**1. Create a folder**

```bash
mkdir -p /opt/syncdrive && cd /opt/syncdrive
```

**2. Create `docker-compose.yml`**

```yaml
services:
  server:
    image: magpeek/syncdrive-server:${SYNCDRIVE_VERSION:-latest}
    restart: unless-stopped
    environment:
      TZ: ${TZ:-Europe/Berlin}
      SYNCDRIVE_Server__AdminUserName: admin
      SYNCDRIVE_Server__AdminPasswordFile: /run/secrets/admin_password
      SYNCDRIVE_Server__TrustForwardedHeaders: "true"   # only Caddy can reach the server
      SYNCDRIVE_Server__RequireTls: "true"
      SYNCDRIVE_Server__BackupDirectory: /backup
      SYNCDRIVE_Server__BackupTime: ${SYNCDRIVE_BACKUP_TIME:-02:30}
    secrets:
      - admin_password
    volumes:
      - syncdrive-data:/data
      - syncdrive-backup:/backup
    read_only: true
    tmpfs:
      - /tmp
    security_opt:
      - no-new-privileges:true
    cap_drop: [ALL]
    networks: [internal]

  proxy:
    image: caddy:2
    restart: unless-stopped
    environment:
      SYNCDRIVE_HOST: ${SYNCDRIVE_HOST:-localhost}
    ports:
      - "8443:8443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
    depends_on:
      server:
        condition: service_healthy
    networks: [internal, default]

secrets:
  admin_password:
    file: ./admin-password.txt

volumes:
  syncdrive-data:
  syncdrive-backup:
  caddy-data:

networks:
  internal:
    internal: true
```

**3. Create `Caddyfile`**

```
{
	auto_https disable_redirects
}

{$SYNCDRIVE_HOST:localhost}:8443 {
	tls internal   # remove this line for a public certificate (needs a DNS name)
	reverse_proxy server:8080 {
		flush_interval -1   # do not buffer long-poll responses
	}
}
```

**4. Create `.env` and the administrator password**

```bash
cat > .env <<'EOF'
SYNCDRIVE_HOST=drive.example.lan
TZ=Europe/Berlin
SYNCDRIVE_BACKUP_TIME=02:30
SYNCDRIVE_VERSION=latest
EOF

printf '%s' 'a-long-admin-password' > admin-password.txt
sudo chown 1654:1654 admin-password.txt && sudo chmod 400 admin-password.txt   # readable for the server user only
```

| Variable in `.env` | Default | Meaning |
|---|---|---|
| `SYNCDRIVE_HOST` | `localhost` | Host name clients use; Caddy issues the certificate for it |
| `TZ` | `Europe/Berlin` | Time zone of the server (backup time, log timestamps) |
| `SYNCDRIVE_BACKUP_TIME` | `02:30` | Time of the nightly backup (`HH:mm`, in `TZ`) |
| `SYNCDRIVE_VERSION` | `latest` | Image tag; pin a version such as `0.18.2` for controlled updates |

**5. Start**

```bash
docker compose up -d
docker compose ps          # server "healthy", proxy "running"
docker compose logs -f server
```

**6. Sign in**

Open `https://drive.example.lan:8443/` and sign in as `admin` with the password from step 4. The administrator is created on the
first start only (when no user exists yet); afterwards you can delete `admin-password.txt` and change the password in the web
interface. Further users, groups and team folders are created in the account menu → *Administration*.

About the certificate: with `tls internal` Caddy issues a certificate from its own CA. Browsers warn once; the Windows client shows
the fingerprint on first connect and asks you to trust it. For a publicly trusted certificate point a DNS name at the server, make
ports 80/443 reachable for the ACME challenge or adapt the Caddyfile, and remove `tls internal`.

The repository contains the same stack in [`deploy/`](../deploy) (with resource limits and log rotation); there
`docker compose up -d --build` builds the image from source instead of pulling it.

## Option B: `docker run` behind your own reverse proxy

Use this when a reverse proxy that already terminates TLS (nginx, Traefik, a NAS proxy) is in place. The server listens on plain
HTTP port 8080; bind it to localhost only.

```bash
mkdir -p /opt/syncdrive && cd /opt/syncdrive
printf '%s' 'a-long-admin-password' > admin-password.txt
sudo chown 1654:1654 admin-password.txt && sudo chmod 400 admin-password.txt

docker run -d --name syncdrive \
  --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -v syncdrive-data:/data \
  -v syncdrive-backup:/backup \
  -v "$PWD/admin-password.txt:/run/secrets/admin_password:ro" \
  -e TZ=Europe/Berlin \
  -e SYNCDRIVE_Server__AdminUserName=admin \
  -e SYNCDRIVE_Server__AdminPasswordFile=/run/secrets/admin_password \
  -e SYNCDRIVE_Server__TrustForwardedHeaders=true \
  -e SYNCDRIVE_Server__BackupDirectory=/backup \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  magpeek/syncdrive-server:latest
```

Then configure the proxy as described in [Reverse proxy and TLS](#reverse-proxy-and-tls).

For a quick local test without TLS add `-e SYNCDRIVE_Server__RequireTls=false` and open `http://localhost:8080/`.
Never run without TLS on a network you do not control.

## Option C: Build the image from source

```bash
git clone https://github.com/MAGPEEK/SyncDrive.git
cd SyncDrive
docker build -t syncdrive-server .                 # image for the current platform
cd deploy && docker compose up -d --build          # or: complete stack from source
```

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
directory must be writable for that UID (`sudo chown 1654 /path/to/dir`). To put the backup on another disk, replace the volume,
e.g. `- /mnt/usb/syncdrive-backup:/backup`.

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

Docker Compose (Option A):

```bash
cd /opt/syncdrive
docker compose pull        # with SYNCDRIVE_VERSION=latest, or set the new version in .env first
docker compose up -d
docker image prune -f      # optional: remove the old image
```

`docker run` (Option B): `docker pull magpeek/syncdrive-server:latest`, then remove the container (`docker rm -f syncdrive`) and
start it again with the same command; the data stays in the volumes.

Database migrations run automatically on start. Clients and server of the same minor version always work together; before 1.0.0 a
new minor version may require updating the clients as well (see [CHANGELOG.md](../CHANGELOG.md)).

## Health and version

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | `200` when the server is up (used by the Docker health check, works without TLS) |
| `GET /api/v1/info` | Product name and version, e.g. `{"product":"SyncDrive","version":"0.18.2"}` |

```bash
curl -k https://drive.example.lan:8443/api/v1/info
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Server stays `unhealthy` / restarts | `docker compose logs server`. Most often `/data` or `/backup` is a bind mount not writable for UID 1654, or `admin-password.txt` is not readable for UID 1654. |
| Sign-in as `admin` fails | The administrator is only created when the database has no users yet; changing the password file later has no effect. Another administrator can set a new password (*Administration → Users*); on a fresh installation, remove the stack with `docker compose down -v` and start again. |
| Every request returns `400 invalid_request` (TLS) | Requests reach the server without `X-Forwarded-Proto: https`. Set `TrustForwardedHeaders=true` and forward the header. |
| Clients disconnect every minute | The proxy buffers or times out long-poll requests: disable buffering, read timeout above 60 s. |
| Caddy fails to start | `SYNCDRIVE_HOST` must be a host name (not empty); port 8443 must be free. |
