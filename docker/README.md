# Docker Compose example

A ready-to-run SyncDrive server using the prebuilt image
[`magpeek/syncdrive-server`](https://hub.docker.com/r/magpeek/syncdrive-server) from Docker Hub — one container, no proxy, no
source code. The application listens on port **8080** (HTTP).

| File | Purpose |
|---|---|
| [`docker-compose.yml`](docker-compose.yml) | The SyncDrive service with its volumes |
| [`.env.example`](.env.example) | Variables (port, administrator, time zone, backup time, version); copy to `.env` |

## Deploy

On a Linux host with Docker and the Compose plugin:

```bash
# copy this folder to the server, e.g. to /opt/syncdrive
cd /opt/syncdrive
cp .env.example .env
nano .env                    # set SYNCDRIVE_ADMIN_PASSWORD
docker compose up -d
docker compose ps            # STATUS "healthy" after a few seconds
```

Open `http://<server>:8080/` and sign in as `admin`. In the Windows client use the address `http://<server>:8080`.

## Operate

```bash
docker compose logs -f                        # logs
docker compose pull && docker compose up -d   # update (or set a new SYNCDRIVE_VERSION in .env first)
docker compose down                           # stop; data stays in the volumes syncdrive-data and syncdrive-backup
```

## HTTPS

Plain HTTP is fine on a trusted home network. Otherwise give the container a certificate (it then serves HTTPS on port 8443) or put
it behind a reverse proxy — both in [docs/server.md](../docs/server.md#https), together with all configuration variables.
