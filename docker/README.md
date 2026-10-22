# Docker Compose example

A ready-to-run SyncDrive stack using the prebuilt image
[`magpeek/syncdrive-server`](https://hub.docker.com/r/magpeek/syncdrive-server) from Docker Hub. No source code or build needed.

| File | Purpose |
|---|---|
| [`docker-compose.yml`](docker-compose.yml) | SyncDrive server (internal network only) and Caddy as TLS reverse proxy on port 8443 |
| [`Caddyfile`](Caddyfile) | Caddy configuration: certificate for `SYNCDRIVE_HOST`, unbuffered proxy to the server |
| [`.env.example`](.env.example) | Variables of the stack; copy to `.env` |

```
            clients / browser
                   │ https://SYNCDRIVE_HOST:8443
           ┌───────▼───────┐
           │  proxy (Caddy) │  TLS
           └───────┬───────┘
     internal net  │ http://server:8080
           ┌───────▼───────┐
           │    server     │  volumes: syncdrive-data → /data
           │  (SyncDrive)  │           syncdrive-backup → /backup
           └───────────────┘
```

## Deploy

On a Linux host with Docker and the Compose plugin:

```bash
# 1. copy this folder to the server, e.g. to /opt/syncdrive, and change into it
cd /opt/syncdrive

# 2. variables
cp .env.example .env
nano .env                                   # set at least SYNCDRIVE_HOST

# 3. administrator password (read once, on the first start)
printf '%s' 'a-long-admin-password' > admin-password.txt
sudo chown 1654:1654 admin-password.txt     # the server runs as UID 1654
sudo chmod 400 admin-password.txt

# 4. start
docker compose up -d
docker compose ps                           # server "healthy", proxy "running"
```

Open `https://<SYNCDRIVE_HOST>:8443/` and sign in as `admin`. Afterwards `admin-password.txt` can be deleted.

## Operate

```bash
docker compose logs -f server               # logs
docker compose pull && docker compose up -d # update (or set a new SYNCDRIVE_VERSION in .env first)
docker compose down                         # stop; data stays in the volumes
```

## Adjust

- **Public certificate:** point a DNS name at the server, set it as `SYNCDRIVE_HOST`, remove the line `tls internal` from the
  `Caddyfile` and make ports 80/443 reachable (or configure a DNS challenge in Caddy).
- **Backup on another disk:** replace `syncdrive-backup:/backup` with a host directory, e.g. `/mnt/usb/syncdrive-backup:/backup`
  (`sudo chown 1654 /mnt/usb/syncdrive-backup`).
- **Further settings** (quotas, Argon2 parameters, block size, long-poll timeout …) are environment variables
  `SYNCDRIVE_Server__*`; add them under `environment:` of the `server` service. All variables are listed in
  [docs/server.md](../docs/server.md#configuration-variables).
- **Existing reverse proxy:** use the `docker run` variant in [docs/server.md](../docs/server.md#option-b-docker-run-behind-your-own-reverse-proxy) instead.
