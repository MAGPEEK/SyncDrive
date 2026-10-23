# SyncDrive on Kubernetes

| File | Purpose |
|---|---|
| [`syncdrive.yaml`](syncdrive.yaml) | Namespace, data volume, Deployment (one replica), Service on port 8080 |
| [`ingress.example.yaml`](ingress.example.yaml) | Example Ingress with TLS (ingress-nginx, cert-manager) |

SyncDrive keeps its database (SQLite) in the data volume, so it runs as **exactly one replica** with a `ReadWriteOnce` volume on
local or block storage (not NFS).

```bash
kubectl apply -f syncdrive.yaml
kubectl -n syncdrive create secret generic syncdrive-admin --from-literal=admin-password='a-long-admin-password'
kubectl -n syncdrive rollout status deployment/syncdrive
```

Then make the Service reachable via HTTPS: adapt and apply `ingress.example.yaml`, or test locally with
`kubectl -n syncdrive port-forward svc/syncdrive 8080` and `SYNCDRIVE_Server__RequireTls=false`.

The administrator is created on the first start only. All configuration variables are listed in the
[server documentation](../docs/server.md#configuration-variables); add them under `env:` in the Deployment.

Update: `kubectl -n syncdrive rollout restart deployment/syncdrive` (with `:latest`), or change the image tag to a new version.
