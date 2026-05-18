# 🏃 RunnerRunner

A self-hosted CI/CD runner orchestration platform for managing GitHub Actions, Gitea Actions, and Azure DevOps runners across a fleet of heterogeneous machines (macOS, Linux, Windows) from a single web UI.

<img width="1331" height="760" alt="image" src="https://github.com/user-attachments/assets/7f4118de-a8cf-49bf-b052-b82fe1bd6bd5" />

## What It Does

RunnerRunner lets you:

- **Declare runner profiles** — pick a CI provider, execution backend, env vars, image config, and labels
- **Assign profiles to hosts** — say "run 3 instances of this profile on that machine" and the system converges
- **Compose environment variables** — build reusable env var sets (e.g. `dotnet-8-sdk`, `xcode-15-env`) and layer them with priority
- **Auto-register/deregister runners** — runners register with GitHub/Gitea/AzDO on startup, deregister on shutdown
- **Orchestrate multiple hosts** — agents connect to the central server from macOS, Linux, or Windows machines

## Architecture

RunnerRunner is a hybrid control plane/data plane system:

```
PostgreSQL
  |-- Orleans clustering, grain state, reminders
  `-- Shiny DocumentDB read model

RunnerRunner.Server
  |-- Blazor UI and webhook API
  `-- Orleans grains for hosts, profiles, provisioning rules, runners

Host machines
    `-- RunnerRunner.HostWorker
        |-- Authenticated outbound gRPC worker connection
        `-- Docker, Tart, or Native runner backends
```

**Server** - Blazor web UI for managing profiles, hosts, env vars, credentials, provisioning rules, and webhook routing. It owns the shared PostgreSQL-backed control plane and sends runner lifecycle commands to hosts.

**HostWorker** - Per-host worker. `HostWorker` connects outbound to the server over authenticated gRPC, receives host commands, writes durable local journals/logs, and executes Docker, Tart, or native runner lifecycles locally.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full system model, including how provisioning rules, profiles, hosts, and runner instances combine to calculate capacity and concurrency.

## Prerequisites

- A Linux machine or VM for the control plane
- Docker Engine with the Docker Compose plugin
- A DNS name or stable IP for the web UI and HostWorkers to reach
- Provider credentials for GitHub Actions, Gitea Actions, or Azure DevOps

## Install the Server

The server is the control plane: PostgreSQL plus `RunnerRunner.Server`, which includes the web UI, API, and Orleans silo. Start with the server, then add HostWorkers after the UI is online.

### Installer Script

```bash
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/install-server.sh -o install-server.sh
chmod +x install-server.sh
sudo ./install-server.sh \
  --orleans-ip 192.168.2.4 \
  --server-port 4779
```

Use the real LAN IP or public IP for `--orleans-ip`. The installer writes `/opt/runnerrunner/compose.yaml`, creates `/opt/runnerrunner/.env`, pulls the published GHCR images, and starts the stack.

Open the UI at `http://192.168.2.4:4779`.

To update:

```bash
sudo runnerrunner-server update
# Or target a specific release tag, branch, or commit SHA
sudo runnerrunner-server update main
```

Useful day-two commands:

```bash
sudo runnerrunner-server status
sudo runnerrunner-server version
sudo runnerrunner-server logs
sudo runnerrunner-server restart
```

### With Compose

Alternatively, use compose directly:

```bash
sudo mkdir -p /opt/runnerrunner
cd /opt/runnerrunner
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/runnerrunner-linux-compose.tar.gz | sudo tar -xz

sudo tee .env >/dev/null <<'ENV'
RUNNERRUNNER_VERSION=latest
POSTGRES_DB=runnerrunner
POSTGRES_USER=runnerrunner
POSTGRES_PASSWORD=<change-me>
LINUX_BIND_IP=0.0.0.0
SERVER_PORT=4779
HOSTWORKER_GRPC_PORT=4780
POSTGRES_PORT=5433
ORLEANS_ADVERTISED_IP=192.168.2.4
HOSTWORKER_ENROLLMENT_TOKEN=<bootstrap-token>
COMPOSE_PROFILES=
ENV

sudo docker compose --env-file .env -f compose.yaml pull
sudo docker compose --env-file .env -f compose.yaml up -d --remove-orphans
```

## Install Host Workers

Create a per-host enrollment token in **Hosts** before installing standalone workers. The server listens for HostWorker gRPC connections on port `4780` by default.

### Linux

Use the installer script when the Linux worker should run in the same compose stack as the server:

```bash
sudo ./install-server.sh \
  --orleans-ip 192.168.2.4 \
  --server-port 4779 \
  --with-linux-worker \
  --enrollment-token '<bootstrap-or-host-token>'
```

For an existing server install, enable the bundled worker profile and restart the compose stack:

```bash
sudo runnerrunner-server enable-linux-worker
```

Or compose a standalone Linux worker on a separate host:

```yaml
services:
  host-worker:
    image: ghcr.io/redth/runnerrunner-hostworker:latest
    container_name: runnerrunner-host-worker
    restart: unless-stopped
    environment:
      HostWorker__ServerUrl: http://runner.example.com:4780
      HostWorker__EnrollmentToken: <host-token-from-hosts-page>
      HostWorker__HostId: linux-worker-01
      HostWorker__HostName: linux-worker-01
      HostWorker__Platform: Linux
      HostWorker__DataRoot: /var/lib/runnerrunner
      DOTNET_ENVIRONMENT: Production
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - hostworker-data:/var/lib/runnerrunner

volumes:
  hostworker-data:
```

```bash
docker compose up -d
docker compose logs -f host-worker
```

### macOS

Use the installer script for native macOS hosts. This supports Docker, Tart, and native macOS runner backends because the HostWorker runs as a LaunchAgent in the interactive user session:

```bash
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/install-host-macos.sh -o install-host-macos.sh
chmod +x install-host-macos.sh
./install-host-macos.sh \
  --host-name mac-mini-01 \
  --server-url 'https://runner.example.com' \
  --enrollment-token '<host-token-from-hosts-page>'
```

Use Docker for Linux Docker containers only:

```bash
docker run -d \
  --name runnerrunner-host-worker \
  --restart unless-stopped \
  -e HostWorker__ServerUrl='http://runner.example.com:4780' \
  -e HostWorker__EnrollmentToken='<host-token-from-hosts-page>' \
  -e HostWorker__HostId='mac-docker-linux-01' \
  -e HostWorker__HostName='mac-docker-linux-01' \
  -e HostWorker__Platform='Linux' \
  -e HostWorker__DataRoot='/var/lib/runnerrunner' \
  -e DOTNET_ENVIRONMENT='Production' \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v runnerrunner-hostworker-data:/var/lib/runnerrunner \
  ghcr.io/redth/runnerrunner-hostworker:latest
```

### Windows

Use the installer script:

```powershell
Invoke-WebRequest https://github.com/redth/RunnerRunner/releases/latest/download/runnerrunner-hostworker-win-x64.zip -OutFile .\runnerrunner-hostworker-win-x64.zip
Invoke-WebRequest https://github.com/redth/RunnerRunner/releases/latest/download/Install-HostWorker.ps1 -OutFile .\Install-HostWorker.ps1
Expand-Archive .\runnerrunner-hostworker-win-x64.zip -DestinationPath 'C:\Program Files\RunnerRunner' -Force
powershell -ExecutionPolicy Bypass -File .\Install-HostWorker.ps1 `
  -HostName windows-build-01 `
  -ServerUrl 'https://runner.example.com' `
  -EnrollmentToken '<host-token-from-hosts-page>'
```

Use Docker for Windows-based runner containers by running the native Windows HostWorker service with Docker Engine in Windows container mode:

```powershell
docker info --format '{{.OSType}}'
# windows

powershell -ExecutionPolicy Bypass -File .\Install-HostWorker.ps1 `
  -HostName windows-docker-01 `
  -ServerUrl 'https://runner.example.com' `
  -EnrollmentToken '<host-token-from-hosts-page>'
```

Then create runner profiles with **Host Platform** = `Windows` and **Execution Backend** = `Docker`.

Or run the HostWorker itself as a Windows container on a Windows Docker host. This uses the separate Windows HostWorker image and the Docker named pipe to launch actual Windows-based runner containers:

```powershell
docker info --format '{{.OSType}}'
# windows

docker run -d `
  --name runnerrunner-host-worker-windows `
  --restart unless-stopped `
  -e 'HostWorker__ServerUrl=http://runner.example.com:4780' `
  -e 'HostWorker__EnrollmentToken=<host-token-from-hosts-page>' `
  -e 'HostWorker__HostId=windows-docker-01' `
  -e 'HostWorker__HostName=windows-docker-01' `
  -e 'HostWorker__Platform=Windows' `
  -e 'DOTNET_ENVIRONMENT=Production' `
  --mount 'type=npipe,source=\\.\pipe\docker_engine,target=\\.\pipe\docker_engine' `
  --mount 'type=volume,source=runnerrunner-hostworker-windows-data,target=C:\ProgramData\RunnerRunner' `
  ghcr.io/redth/runnerrunner-hostworker-windows:latest
```

Use Docker for Linux containers by running the Linux HostWorker container against Docker Desktop's Linux engine from WSL:

```bash
docker run -d \
  --name runnerrunner-host-worker \
  --restart unless-stopped \
  -e HostWorker__ServerUrl='http://runner.example.com:4780' \
  -e HostWorker__EnrollmentToken='<host-token-from-hosts-page>' \
  -e HostWorker__HostId='windows-linux-docker-01' \
  -e HostWorker__HostName='windows-linux-docker-01' \
  -e HostWorker__Platform='Linux' \
  -e HostWorker__DataRoot='/var/lib/runnerrunner' \
  -e DOTNET_ENVIRONMENT='Production' \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v runnerrunner-hostworker-data:/var/lib/runnerrunner \
  ghcr.io/redth/runnerrunner-hostworker:latest
```

## Getting Started

### 1. Configure Provider Credentials

Navigate to **Credentials** in the web UI and add your CI provider credentials. The Add Credential wizard starts with the credential type, then walks through the provider setup before collecting fields:

| Provider | Required Fields |
|---|---|
| **GitHub Actions** | PAT scoped to an owner/repo, or a GitHub App identity with one or more installation targets |
| **Gitea Actions** | Instance URL, Runner Token (from Gitea admin) |
| **Azure DevOps** | Organization URL, Project Name, PAT, Pool Name |

For GitHub App credentials, create an app with the RunnerRunner webhook URL (`/api/webhooks/github`), subscribe to `workflow_job`, grant self-hosted runner management permissions, and install it in each org, user-owned repository, or repository RunnerRunner should manage. One credential stores the app identity plus multiple installation targets; RunnerRunner uses the webhook installation ID for dynamic jobs and falls back to the credential's default installation target for static runners and reconciliation.

### 2. Create Environment Variable Sets

Go to **Env Variable Sets** and create reusable sets:

```
Name: dotnet-8-sdk
Priority: 1
Variables:
  DOTNET_ROOT=/usr/share/dotnet
  DOTNET_CLI_TELEMETRY_OPTOUT=1
  DOTNET_NOLOGO=1
```

```
Name: android-sdk
Priority: 2
Variables:
  ANDROID_HOME=/usr/local/lib/android/sdk
  ANDROID_SDK_ROOT=/usr/local/lib/android/sdk
```

Higher priority sets win when keys conflict. Profiles compose multiple sets.

### 3. Create Runner Profiles

Go to **Runner Profiles** and create a profile:

| Field | Example |
|---|---|
| Name | `github-linux-builder` |
| Provider | GitHub Actions |
| Credential | (select from dropdown) |
| Host Platform | Linux |
| Execution Backend | Docker |
| Labels | `linux, docker, x64` |
| Max Parallel Per Host | 4 |
| Docker Registry | `ghcr.io` |
| Docker Image | `myorg/runner-image` |
| Docker Tag | `latest` |
| Env Var Sets | ✅ dotnet-8-sdk, ✅ android-sdk |

### 4. Assign Profiles to Hosts

Go to **Hosts**, click **Assign** on a connected host, select a profile and desired instance count. The orchestration engine will automatically deploy runners.

### 5. Understand provisioning and concurrency

RunnerRunner has two provisioning paths:

- **Static assignments** - host/profile pairs with a fixed desired count. These are reconciled by the legacy orchestration engine.
- **Provisioning rules** - the newer model for Static, ScaleSet, Webhook/JIT, and future Scheduled provisioning. Rules can target hosts by platform, host ID, host group, or labels.

Capacity is calculated from the narrowest remaining limit:

1. FIFO ordering for queued webhook jobs in the same rule or platform/backend lane.
2. Rule-level capacity: `DesiredCount`, `MaxInstances`, or `MaxConcurrent`.
3. Profile-level per-host capacity: `MaxParallelPerHost`.
4. Host backend capacity: `MaxDockerContainers`, `MaxTartVMs`, or `MaxNativeProcesses`.

For a profile mapped to a rule, the effective pool limit is the sum across matching hosts of `min(profile per-host limit, host backend limit)`. If a webhook rule allows 10 concurrent jobs, but the matching pool has 3 hosts and the profile allows 1 runner per host, only 3 runners can start at once. A backend limit of `0` disables that backend on that host.

### 6. Runner image/backend info in the GHA job log

Every runner deployed by RunnerRunner surfaces its backend, image, host, and
profile info into the provider's job log through two complementary channels:

1. **`rr-*` metadata labels** — appended to the runner's label set so they
   show up in the "Set up job" block's `Labels:` list:

   ```
   rr-backend:docker
   rr-provider:GitHubActions
   rr-profile:github-linux-builder
   rr-host:mac-studio-01
   rr-image:ghcr.io-myorg-runner-image
   rr-tag:latest
   ```

   These are informational only — don't match them with `runs-on`. Toggle
   per-profile via **Emit `rr-*` metadata labels** on the profile editor
   (default: on).

2. **Job-started banner hook** — RunnerRunner installs an
   `ACTIONS_RUNNER_HOOK_JOB_STARTED` script inside every runner (bash on
   Linux/macOS, PowerShell on Windows). It renders a collapsible
   "RunnerRunner environment" section at the top of every job log:

   ```
   ▸ RunnerRunner environment
     Backend:         docker
     Host:            mac-studio-01
     Profile:         github-linux-builder
     Provider:        GitHubActions
     Image:           ghcr.io/myorg/runner-image:latest
     Agent version:   2.333.1
     Instance:        7f1e8b52-...
   ```

   The hook script itself is static and reads `RR_META_*` env vars the
   server seeds into the deploy command, so it works identically across
   Docker (bind-mounted read-only), Tart (SCP'd into the guest), and
   Native (written to the per-instance directory). Toggle per-profile
   via **Install job-started banner hook** (default: on).

**Caveats**

- GitHub's "Set up job" header itself (runner name, runner version, OS,
  machine name) is emitted by `actions/runner` — RunnerRunner cannot
  inject extra lines into that specific block.
- For dynamic (JIT) runs, the label set is baked into the JIT
  registration token before the agent pulls the image, so the image
  digest isn't currently included in `rr-*` labels.

## Release artifacts and updates

RunnerRunner uses GitHub Releases as the public distribution and update catalog. Releases publish the Linux server compose bundle, HostWorker packages, install scripts, checksums, and `release-manifest.json`. There is no legacy SignalR agent package.

The checked-in SSH deploy helpers are not the recommended release path. They are kept for development and debugging when you need to push local builds to a lab host.

### Published Docker images

RunnerRunner publishes multi-architecture `linux/amd64` and `linux/arm64` images plus a Windows HostWorker image to GitHub Container Registry:

| Image | Use |
|---|---|
| `ghcr.io/redth/runnerrunner-server:<tag>` | Server, web UI, API, and Orleans silo |
| `ghcr.io/redth/runnerrunner-hostworker:<tag>` | Self-contained Linux HostWorker for Docker-backed runner hosts |
| `ghcr.io/redth/runnerrunner-hostworker-windows:<tag>` | Self-contained Windows HostWorker for Windows Docker hosts running Windows-based runner containers |

Tagged releases publish `<tag>`, `<git-sha>`, and `latest`. The `main` branch image workflow publishes `main` and `<git-sha>` for early adopters and lab installs.

### Server and HostWorker updates

The **Settings** page shows the server assembly version, informational version, commit SHA, and build tag. Server installs update from the host with `runnerrunner-server update [ref]`, where `ref` can be `latest`, a release tag, a branch, or a commit SHA. Branch refs resolve through the configured GitHub repository and then update to the matching published commit image.

### HostWorker updates from the UI

The server can update HostWorkers from two sources:

- **GitHub ref** — `latest`, a release tag, branch name, or commit SHA from the configured source repository. Release tags use GitHub Release assets first; branches and commits resolve to the matching successful workflow artifacts.
- **SSH/local folder** — artifacts copied into the server's local artifact folder, usually by `scp` or lab automation.

On the **Hosts** page, choose the source and ref/version, click **Check**, then apply the selected build to online workers. Local-folder builds are intentionally allowed to reinstall the same version string so debug builds can move back and forth without creating public releases.

Native workers download the matching artifact for their platform, verify the SHA256 from the selected source, stage the update under their data root, and restart through the platform service layer. Containerized HostWorkers use the manifest's matching GHCR image instead: the current container pulls the target image, creates a replacement container with the same environment, mounts, network, and restart policy, starts it, then stops the old container. A later `docker compose up` can still recreate the worker from the compose file image tag.

The same flow is exposed through token-protected API endpoints. Use the HostWorker enrollment token as `Authorization: Bearer <token>` or `X-RunnerRunner-Enrollment-Token: <token>`.

```bash
# List local-folder builds
curl -H "Authorization: Bearer ${ENROLLMENT_TOKEN}" \
  "https://runner.example.com/api/hostworker-updates?source=local-folder"

# Queue a GitHub ref for a host
curl -H "Authorization: Bearer ${ENROLLMENT_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{"source":"release","version":"main"}' \
  https://runner.example.com/api/hostworker-updates/hosts/mac-mini-01/update
```

## Docker Compose Topology

The published Linux compose bundle is the recommended deployment shape:

| Service | Purpose |
|---|---|
| `postgres` | PostgreSQL 17 for DocumentDB, Orleans clustering, grain state, and reminders |
| `server` | Blazor web UI, webhook API, Orleans server silo |
| `host-worker` | Optional Linux worker enabled with `COMPOSE_PROFILES=linux-worker` |

Key ports:

| Port | Service |
|---|---|
| `4779` | RunnerRunner web UI and API |
| `4780` | HostWorker gRPC over cleartext HTTP/2 |
| `5433` | PostgreSQL published on the host by default; containers use `5432` |
| `11111` / `30000` | Server Orleans silo/gateway |

The compose file wires the server to PostgreSQL. When the optional worker profile is enabled, the HostWorker connects back to the server over authenticated outbound gRPC:

```text
Database__ConnectionString=Host=postgres;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner
HostWorker__ServerUrl=http://server:4780
HostWorker__EnrollmentToken=<bootstrap-token-or-host-token>
```

> **Note:** macOS hosts cannot use the Linux HostWorker container to control Tart, Xcode, or Keychain resources. Deploy the macOS HostWorker as a native binary and point it at the HostWorker gRPC URL, for example `http://192.168.2.4:4780`.

### Optional observability stack

The Linux compose bundle can run an OpenTelemetry Collector profile for traces, metrics, and structured logs:

```bash
cd /opt/runnerrunner
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317 docker compose --env-file .env -f compose.yaml --profile observability up -d
```

The collector defaults to a debug exporter so self-hosted installs get a non-blocking OTLP endpoint without taking a dependency on a full logging platform. Point `OTEL_EXPORTER_OTLP_ENDPOINT` at Grafana Alloy/Collector, Seq, Azure Monitor, or another OTLP-compatible sink for production retention and querying.

### Development/debug deploy helpers

The scripts under `deploy/` that build locally and copy artifacts over SSH are intended for lab and debugging workflows only. Public installs should use GitHub Release artifacts, `runnerrunner-server update`, and the HostWorker update controls in the **Hosts** page so installed services stay aligned with the release manifest and platform service managers.

## Execution Backends

| Backend | Platform | How It Works |
|---|---|---|
| **Docker** | Linux, Windows | Spins up a container per runner instance. Image provides the runner environment. Registration happens in the container's entrypoint. |
| **Tart** | macOS | Clones an OCI VM image, configures via shared directory `.env` file, starts the VM. Used for macOS CI (Xcode builds, iOS testing). |
| **Native** | Any | Runs the runner agent binary directly as a child process. Multiple runners share the host OS. Best for bare-metal macOS. |

## Environment Variable Composition

Env vars are composed in layers (later layers win on key conflicts):

```
Layer 1: Provider credentials (auto-injected RR_* values)
   RR_GITHUB_TOKEN=...

Layer 2: Env Var Sets (ordered by priority)
   dotnet-8-sdk (priority 1): DOTNET_ROOT=/usr/share/dotnet
   android-sdk (priority 2): ANDROID_HOME=/opt/android

Layer 3: Profile overrides
   DOTNET_ROOT=/custom/dotnet

Layer 4: Host overrides
   ANDROID_HOME=/local/android

Layer 5: Instance values
   RR_INSTANCE_ID=...
   RR_RUNNER_NAME=profile-abc123
```

After composition, `$VAR` and `${VAR}` references are expanded so values can chain through earlier layers.

## Configuration Reference

### Server

| Setting | Default | Description |
|---|---|---|
| `Database:ConnectionString` | `Host=localhost;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner` | PostgreSQL connection string for DocumentDB and Orleans |
| `ConnectionStrings:DefaultConnection` | *(empty)* | Alternate PostgreSQL connection string source |
| `DataProtection:KeysPath` | `{ContentRoot}/data/data-protection-keys` | Persistent ASP.NET Core data-protection key ring for antiforgery and Blazor server state in production |
| `Kestrel:Endpoints:Web:Url` | `http://+:4779` | Web UI and API listen URL |
| `Kestrel:Endpoints:HostWorkerGrpc:Url` | `http://+:4780` | HostWorker gRPC listen URL |
| `Orleans:AdvertisedIPAddress` | *(empty)* | External IP advertised by the server Orleans silo in production |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(empty)* | Optional OTLP endpoint for logs, metrics, and traces |
| `HostWorkerUpdates:Repository` | `Redth/RunnerRunner` | GitHub repository used for HostWorker update checks |
| `HostWorkerUpdates:GitHubToken` | *(empty)* | Optional GitHub token used for private repositories, higher API limits, and workflow artifact downloads; overrides stored credentials |
| `HostWorkerUpdates:ProviderCredentialId` | *(empty)* | Optional GitHub Actions credential ID to use for update checks; otherwise the server uses a matching stored GitHub App/PAT credential when available |
| `HostWorkerUpdates:CacheMinutes` | `30` | How long the latest GitHub release check is cached |
| `HostWorkerUpdates:ManifestArtifactName` | `runnerrunner-hostworker-manifest` | GitHub Actions artifact containing `release-manifest.json` for branch/commit update refs |
| `HostWorkerUpdates:AssetsArtifactName` | `runnerrunner-hostworker-assets` | GitHub Actions artifact containing native HostWorker archives for branch/commit update refs |
| `HostWorkerUpdates:StorageRoot` | `{ContentRoot}/data/hostworker-updates` | Root for local-folder HostWorker update artifacts |
| `HostWorkerUpdates:LocalArtifactRoot` | `{StorageRoot}/local` | SSH/local-folder artifact root; place assets under version subfolders |
| `HostWorkerUpdates:PublicBaseUrl` | Current request URL | External server URL used in HostWorker artifact download commands when not queued from an HTTP request |
| `Logging:Console:FormatterName` | `json` outside Development; compose bundles set `simple` | Console log format; set to `simple` for Docker/Dockhand-style text logs or `json` for stdout log shippers |
| `Logging:Console:IncludeScopes` | `true` for JSON, `false` in compose bundles | Include activity/request scopes in console logs |

### HostWorker

| Setting | Default | Description |
|---|---|---|
| `HostWorker:ServerUrl` | `http://localhost:5000` | RunnerRunner server URL for the outbound gRPC connection |
| `HostWorker:EnrollmentToken` | *(empty)* | Per-host token generated from the Hosts page, or the bootstrap token for bundled installs |
| `HostWorker:HostId` | Machine hostname | Stable host identity |
| `HostWorker:HostName` | Machine hostname | Display name for this host |
| `HostWorker:Platform` | Current OS | Optional platform override reported to the server |
| `HostWorker:Architecture` | Current process architecture | Optional architecture override reported to the server |
| `HostWorker:DataRoot` | Platform default | Durable command journal and runner metadata root |
| `HostWorker:LogRoot` | `<DataRoot>/logs` | Durable worker/runner log root |
| `HostWorker:RestartCommand` | *(empty)* | Optional Unix command run by the self-update handoff script after files are copied |
| `HostWorker:WindowsServiceName` | `RunnerRunnerHostWorker` | Windows service name used by the self-update handoff script |
| `HostWorker:ContainerImage` | *(empty)* | Optional image reference reported by containerized HostWorkers so the Hosts page can show the currently configured container image |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(empty)* | Optional OTLP endpoint for logs, metrics, and traces |
| `Logging:Console:FormatterName` | `json` outside Development; compose bundles set `simple` | Console log format; set to `simple` for Docker/Dockhand-style text logs or `json` for stdout log shippers |
| `Logging:Console:IncludeScopes` | `true` for JSON, `false` in compose bundles | Include activity/request scopes in console logs |

All settings can be provided via:
- `appsettings.json`
- Environment variables (e.g. `HostWorker__HostName`)
- Command-line args (e.g. `--HostWorker:HostName=mac-mini-01`)

### Logging and build metadata

RunnerRunner logs startup metadata immediately when each app starts, including assembly version, informational version, commit SHA, build tag, environment, OS, framework, machine name, content root, and process ID. Standalone non-development runs use one-line JSON console logs by default so Docker and log shippers can parse each event cleanly. The bundled compose files set `LOGGING_CONSOLE_FORMATTER_NAME=simple` and `LOGGING_CONSOLE_INCLUDE_SCOPES=false` so `docker compose logs`, Dockhand, and similar console viewers show readable text by default; set `LOGGING_CONSOLE_FORMATTER_NAME=json` when stdout JSON is preferred.

Container builds accept these optional build arguments and also stamp them into OCI image labels:

| Build arg | Description |
|---|---|
| `INFORMATIONAL_VERSION` | Version shown in startup logs and HostWorker version reporting |
| `SOURCE_REVISION_ID` | Git commit SHA shown in startup logs and image labels |
| `BUILD_TAG` | Git tag or channel name shown in startup logs and image labels |

## Development

### Build

```bash
dotnet build
```

### Test

```bash
dotnet test
```

Tests are split across:
- **Core.Tests** — model defaults and initialization behavior
- **Server.Tests** — provider APIs, orchestration, provisioning, capacity planning, webhooks, DocumentStore integration
- **Agent.Tests** — lifecycle manager, backends, health reporter, job hook scripts

### Project Structure

```
src/
├── RunnerRunner.AppHost/           # Aspire orchestrator (local dev)
├── RunnerRunner.ServiceDefaults/   # Shared OTEL, health checks, service discovery
├── RunnerRunner.Core/              # Domain models, interfaces, command/event contracts
├── RunnerRunner.Server/            # Blazor web UI + orchestration engine
├── RunnerRunner.HostWorker/          # Authenticated per-host worker execution path
└── RunnerRunner.Agent/             # Reusable runner backend/lifecycle library

tests/
├── RunnerRunner.Core.Tests/
├── RunnerRunner.Server.Tests/
└── RunnerRunner.Agent.Tests/
```

## License

MIT
