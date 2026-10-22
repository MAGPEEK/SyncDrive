# SyncDrive with Ansible

The playbook runs the SyncDrive server as a Docker container on the hosts of the group `syncdrive` — the same setup as the
[Docker Compose example](../docker).

| File | Purpose |
|---|---|
| [`syncdrive.yml`](syncdrive.yml) | Playbook: volumes, container, health check |
| [`inventory.example.ini`](inventory.example.ini) | Example inventory |

Requirements: Docker on the target host, Ansible 2.15+ with the `community.docker` collection on the control machine, the Docker
SDK for Python on the target.

```bash
ansible-galaxy collection install community.docker
cp inventory.example.ini inventory.ini        # enter your server
SYNCDRIVE_ADMIN_PASSWORD='a-long-admin-password' ansible-playbook -i inventory.ini syncdrive.yml
```

Variables (override with `-e` or in `group_vars/syncdrive.yml`, the password preferably with `ansible-vault`):

| Variable | Default | Meaning |
|---|---|---|
| `syncdrive_version` | `latest` | Image tag |
| `syncdrive_port` | `8080` | Port on the host |
| `syncdrive_admin_user` / `syncdrive_admin_password` | `admin` / `$SYNCDRIVE_ADMIN_PASSWORD` | Administrator created on the first start |
| `syncdrive_require_tls` | `false` | `true` behind a reverse proxy or with a certificate |
| `syncdrive_timezone` | `Europe/Berlin` | Time zone |
| `syncdrive_backup_time` | `02:30` | Time of the nightly backup |
| `syncdrive_extra_env` | `{}` | Further variables, e.g. `{SYNCDRIVE_Server__BackupsToKeep: "14"}` |

Running the playbook again with a new `syncdrive_version` (or with `latest`) updates the server; the data stays in the volumes.
