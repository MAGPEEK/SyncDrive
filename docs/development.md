# Development

For contributors who want to build, test or change SyncDrive. To run it, use the Docker image and the release downloads
(see the [README](../README.md#usage)).

## Requirements

- [.NET SDK 9](https://dotnet.microsoft.com/download)
- Docker, for the server image
- Windows, for the client packages (WiX 5 is restored automatically as NuGet package)

## Build and test

```bash
dotnet build
dotnet test                              # ~2,500 tests; CI runs them on Linux and Windows
dotnet format --verify-no-changes        # code style, checked in CI
docker build -t syncdrive-server .       # server image incl. web interface
pwsh installer/build-client.ps1          # Windows: client .msi and .exe into artifacts/client
```

Run the server locally without Docker (web interface under `/`):

```bash
SYNCDRIVE_Server__RequireTls=false SYNCDRIVE_Server__DataDirectory=./data \
SYNCDRIVE_Server__AdminUserName=admin SYNCDRIVE_Server__AdminPassword=dev-password \
dotnet run --no-launch-profile --project src/SyncDrive.Server.Host
```

The folder [`deploy/`](../deploy) contains a Compose stack that builds the image from source and puts Caddy (TLS on port 8443)
in front of it: `cd deploy && docker compose up -d --build`.

## Project structure

| Project | Purpose |
|---|---|
| `src/SyncDrive.Core` | Domain logic without dependencies: paths, hashes, merge, filters, conflict names, FastCDC |
| `src/SyncDrive.Contracts` | Protocol DTOs and error codes |
| `src/SyncDrive.Server.*` | Server application services and ASP.NET Core host |
| `src/SyncDrive.Client.*` | Sync engine, background service (daemon), tray UI (Avalonia), `syncdrive_cli` |
| `src/SyncDrive.Web*` | Web interface (Blazor WebAssembly) and its view models |
| `src/SyncDrive.Infra.*` | Adapters: SQLite, block files, Argon2, HTTP, named pipes, Windows Cloud Files, thumbnails |
| `tests/` | Unit, contract, scenario, end-to-end and architecture tests |
| `docker/` | Docker Compose example with the Docker Hub image |
| `deploy/` | Compose stack built from source, with Caddy, resource limits and log rotation |
| `installer/` | WiX 5 projects and build script for the Windows packages |

The architecture is hexagonal: the core knows nothing about databases, HTTP or Windows; architecture tests enforce the dependency rules.

## Releases

The version lives in `Directory.Build.props` and must match the top entry of `CHANGELOG.md` (a test checks this). Pushing a tag
`vX.Y.Z` runs `.github/workflows/release.yml`: it builds the Windows packages, pushes the multi-arch server image to Docker Hub
and creates a GitHub release.

## Code signing (Windows)

Smart App Control and SmartScreen block unsigned programs. `installer/sign.ps1` signs every unsigned EXE and DLL of the client, the MSI
and the setup (including its Burn engine) with [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) (formerly
Trusted Signing). Without `SYNCDRIVE_SIGNING_ACCOUNT` the build stays unsigned, so local builds need no Azure access.

One-time setup in Azure:

1. Create an Artifact Signing account, complete the identity validation and create a certificate profile (*Public Trust*).
2. Register an app in Microsoft Entra ID and add a federated credential for GitHub Actions: organisation `MAGPEEK`, repository
   `SyncDrive`, entity *Environment*, name `release`.
3. Give that app the role *Artifact Signing Certificate Profile Signer* (formerly *Trusted Signing Certificate Profile Signer*) on the
   certificate profile.

Then, in GitHub under *Settings → Secrets and variables → Actions → Variables*, add:

| Variable | Example |
|---|---|
| `ARTIFACT_SIGNING_ENDPOINT` | `https://weu.codesigning.azure.net/` (region of the signing account) |
| `ARTIFACT_SIGNING_ACCOUNT` | name of the signing account |
| `ARTIFACT_SIGNING_PROFILE` | name of the certificate profile |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` | application (client) ID and directory (tenant) ID of the app |

The next tag signs the Windows packages. To sign a local build, run `az login` and set the same values as `SYNCDRIVE_SIGNING_ENDPOINT`,
`SYNCDRIVE_SIGNING_ACCOUNT` and `SYNCDRIVE_SIGNING_PROFILE` before `pwsh installer/build-client.ps1`.
