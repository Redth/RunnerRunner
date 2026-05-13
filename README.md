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

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker (for Docker-based runners and docker-compose deployment)
- [Aspire CLI](https://learn.microsoft.com/dotnet/aspire/fundamentals/setup-tooling) (optional, for local dev)

### Option 1: Aspire (Local Development — Recommended)

The easiest way to run everything locally with a full dashboard:

```bash
# Install Aspire CLI if you haven't
dotnet tool install -g aspire

# Run the full stack
cd src/RunnerRunner.AppHost
aspire run
```

This starts:
- **Server** on `http://localhost:5080` — the web UI
- **HostWorker** for host-local runner execution
- **Aspire Dashboard** with real-time logs, traces, and metrics for all services

### Option 2: Install from a GitHub Release (Linux Server)

For production-style installs, use the published release bundle. It runs the server, PostgreSQL, and an optional bundled Linux HostWorker with Docker Compose and configures `runnerrunner-server update` as the supported update command.

```bash
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/install-server.sh -o install-server.sh
chmod +x install-server.sh
sudo ./install-server.sh --orleans-ip 192.168.2.4 --enrollment-token '<bootstrap-token>'
```

Update the Linux server and bundled Linux HostWorker with:

```bash
sudo runnerrunner-server update
```

### Option 3: Add Native HostWorkers

Create a per-host enrollment token from the **Hosts** page, then install the native HostWorker package for each macOS or Windows machine. Native HostWorkers update from the **Hosts** page after the server checks GitHub Releases.

See [Release installs](#release-installs) for macOS and Windows commands.

### Option 4: Local Docker Compose

For local standalone testing without Aspire:

```bash
docker compose up -d
```

The server is available at `http://localhost:4779` by default.

### Option 5: Manual (dotnet run)

```bash
# Terminal 1: Start the server
cd src/RunnerRunner.Server
dotnet run

# Terminal 2: Start a local HostWorker
cd src/RunnerRunner.HostWorker
dotnet run -- --HostWorker:ServerUrl=http://localhost:5000 --HostWorker:EnrollmentToken=<host-token>
```

## Getting Started

### 1. Configure Provider Credentials

Navigate to **Settings** in the web UI and add your CI provider credentials:

| Provider | Required Fields |
|---|---|
| **GitHub Actions** | Organization, PAT (with `admin:org` or `repo` scope) |
| **Gitea Actions** | Instance URL, Runner Token (from Gitea admin) |
| **Azure DevOps** | Organization URL, Project Name, PAT, Pool Name |

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

## Release installs

RunnerRunner uses GitHub Releases as the public distribution and update catalog. Releases publish the Linux server compose bundle, HostWorker packages, install scripts, checksums, and `release-manifest.json`. There is no legacy SignalR agent package.

For standalone macOS, Windows, or additional Linux workers, generate a per-host token from the **Hosts** page and use it as the worker's enrollment token.

### Linux server and bundled Linux HostWorker

```bash
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/install-server.sh -o install-server.sh
chmod +x install-server.sh
sudo ./install-server.sh --orleans-ip 192.168.2.4 --enrollment-token '<bootstrap-token>'
```

Updates are intentionally one command:

```bash
sudo runnerrunner-server update
```

The release compose bundle consumes published GHCR images and runs PostgreSQL, `RunnerRunner.Server`, and a Linux `RunnerRunner.HostWorker`.

The checked-in SSH deploy helpers are not the recommended release path. They are kept for development and debugging when you need to push local builds to a lab host.

### HostWorker updates from the UI

The server checks GitHub Releases for the latest `runnerrunner-hostworker-*` assets. On the **Hosts** page, click **Check HostWorker Updates** to compare each connected worker's reported version with the latest release. If a native worker has an update, click **Update** to send it an update command.

Native workers download the matching release asset for their platform, verify the SHA256 from `release-manifest.json`, stage the update under their data root, and restart through the platform service layer. Containerized Linux workers are updated by the compose/server update path instead of self-mutating their container image.

### Linux HostWorker

Linux Docker-backed hosts should run the HostWorker container with the host Docker socket mounted. The bundled Linux compose stack already does this. Native `linux-x64` and `linux-arm64` HostWorker tarballs are also published for hosts that need native process execution.

### macOS HostWorker

macOS uses a native LaunchAgent, not Docker. Docker on macOS runs Linux containers inside a VM and cannot reliably control Tart, Xcode, Keychain, or native macOS runner processes on the host.

```bash
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/install-host-macos.sh -o install-host-macos.sh
chmod +x install-host-macos.sh
./install-host-macos.sh \
  --host-name mac-mini-01 \
  --server-url 'https://runner.example.com' \
  --enrollment-token '<host-token-from-hosts-page>'
```

### Windows HostWorker

Windows uses a native Windows Service package by default. Windows Docker mode is not the main release path.

```powershell
Expand-Archive .\runnerrunner-hostworker-win-x64.zip -DestinationPath 'C:\Program Files\RunnerRunner' -Force
powershell -ExecutionPolicy Bypass -File .\Install-HostWorker.ps1 `
  -HostName windows-build-01 `
  -ServerUrl 'https://runner.example.com' `
  -EnrollmentToken '<host-token-from-hosts-page>'
```

## Docker Compose Topology

The checked-in `docker-compose.yml` runs the production-like local topology:

| Service | Purpose |
|---|---|
| `postgres` | PostgreSQL 17 for DocumentDB, Orleans clustering, grain state, and reminders |
| `server` | Blazor UI, webhook API, Orleans server silo |
| `host-worker` | Authenticated outbound worker with local runner execution |

Key ports:

| Port | Service |
|---|---|
| `4779` | RunnerRunner web UI and API |
| `5432` | PostgreSQL |
| `11111` / `30000` | Server Orleans silo/gateway |

The compose file wires the server to PostgreSQL and the HostWorker to the server over gRPC:

```text
Database__ConnectionString=Host=postgres;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner
HostWorker__ServerUrl=http://server:4779
HostWorker__EnrollmentToken=<bootstrap-token-or-host-token>
```

> **Note:** macOS hosts cannot use the Linux HostWorker container to control Tart, Xcode, or Keychain resources. Deploy the macOS HostWorker as a native binary and point it at the server URL.

### Optional observability stack

The Linux compose bundle can run an OpenTelemetry Collector profile for traces, metrics, and structured logs:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317 docker compose --profile observability up -d
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
| `ASPNETCORE_URLS` | `http://localhost:5000` | Server listen URL |
| `Orleans:AdvertisedIPAddress` | *(empty)* | External IP advertised by the server Orleans silo in production |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(empty)* | Optional OTLP endpoint for logs, metrics, and traces |
| `HostWorkerUpdates:Repository` | `Redth/RunnerRunner` | GitHub repository used for HostWorker update checks |
| `HostWorkerUpdates:CacheMinutes` | `30` | How long the latest GitHub release check is cached |

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
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(empty)* | Optional OTLP endpoint for logs, metrics, and traces |

All settings can be provided via:
- `appsettings.json`
- Environment variables (e.g. `HostWorker__HostName`)
- Command-line args (e.g. `--HostWorker:HostName=mac-mini-01`)

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
