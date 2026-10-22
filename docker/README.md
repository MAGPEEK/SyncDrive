# Docker Compose example

A ready-to-run SyncDrive server using the prebuilt image
[`magpeek/syncdrive-server`](https://hub.docker.com/r/magpeek/syncdrive-server) from Docker Hub — one container, one file.
The application listens on port **8080** (HTTP).

## Deploy

1. Copy [`docker-compose.yml`](docker-compose.yml) to your server (or paste it into Portainer / your NAS as a stack).
2. Replace `change-me-to-a-long-password` with your administrator password.
3. Start:

```bash
docker compose up -d
docker compose ps            # STATUS "healthy" after a few seconds
```

Open `http://<server>:8080/` and sign in as `admin`. In the Windows client use the address `http://<server>:8080`.

## Adjust

| Setting in `docker-compose.yml` | Change it to |
|---|---|
| `"8080:8080"` | Use another port on the host: `"9000:8080"` (only the left number) |
| `image: …:latest` | Pin a version, e.g. `magpeek/syncdrive-server:0.18.7` |
| `TZ`, `BackupTime` | Your time zone and the time of the nightly backup |

The administrator password is only read on the first start; change it later in the web interface. All further variables are
listed in the [README](../README.md#configuration) and in [docs/server.md](../docs/server.md#configuration-variables).

## Operate

```bash
docker compose logs -f                        # logs
docker compose pull && docker compose up -d   # update
docker compose down                           # stop; data stays in the volumes syncdrive-data and syncdrive-backup
```

## HTTPS

Plain HTTP is fine on a trusted home network. Otherwise give the container a certificate (it then serves HTTPS on port 8443) or put
it behind a reverse proxy — see [docs/server.md](../docs/server.md#https).
