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
  `-- RunnerRunner.HostSilo
        |-- Orleans cluster member
        `-- Docker, Tart, or Native runner backends
```

**Server** - Blazor web UI for managing profiles, hosts, env vars, credentials, provisioning rules, and webhook routing. It owns the shared PostgreSQL-backed control plane and sends runner lifecycle commands to hosts.

**HostSilo** - Per-host worker. `HostSilo` joins the Orleans cluster, receives host commands through Orleans streams, and executes Docker, Tart, or native runner lifecycles locally.

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
- **HostSilo** for host-local runner execution
- **Aspire Dashboard** with real-time logs, traces, and metrics for all services

### Option 2: Deploy Everything (One Command)

Deploy the full stack to your Linux host plus optional macOS/Windows HostSilo hosts in one shot:

```bash
./deploy/deploy-all.sh
```

This will:
1. Build Docker images for Server + Linux HostSilo
2. Push to your container registry (default: `ghcr.io/redth/runnerrunner`)
3. SSH into the Linux host, deploy via `docker compose up`
4. Publish native HostSilo binaries, SSH into macOS/Windows hosts, and install them as host services

**Configure hosts** via environment variables:

```bash
LINUX_HOST=192.168.2.2 \
MACOS_HOST=192.168.2.134 \
./deploy/deploy-all.sh
```

All defaults are in the script — edit `deploy/deploy-all.sh` or override with env vars.

**Prerequisites:** `docker login ghcr.io` locally, Docker on the Linux host, SSH access to both hosts.

### Option 3: Deploy to Remote Linux Host (via SSH)

Deploy the full stack to a remote Docker host with a single command:

```bash
# Deploy (will prompt for SSH password)
aspire deploy
```

This builds container images, pushes them to your registry, and runs `docker compose up` on the remote host via SSH.

**Configuration** is in `src/RunnerRunner.AppHost/appsettings.json`:

```json
{
  "DockerSSH": {
    "TargetHost": "192.168.2.2",
    "SshUsername": "root"
  },
  "DockerRegistry": {
    "RegistryUrl": "ghcr.io",
    "RepositoryPrefix": "your-org/runnerrunner"
  }
}
```

Or override with environment variables:

```bash
DockerSSH__TargetHost=192.168.2.2 \
DockerSSH__SshUsername=root \
DockerSSH__SshPassword=your-password \
aspire deploy
```

**First time setup:**
1. Ensure Docker is installed on the remote host
2. Log in to your container registry locally: `docker login ghcr.io`
3. Run `aspire deploy` — it handles everything else

**Teardown remote deployment:**
```bash
aspire do teardown-env
```

### Option 4: Docker Compose (Production / Standalone)

```bash
docker compose up -d
```

The server is available at `http://localhost:4779` by default.

### Option 5: Manual (dotnet run)

```bash
# Terminal 1: Start the server
cd src/RunnerRunner.Server
dotnet run

# Terminal 2: Start a local HostSilo
cd src/RunnerRunner.HostSilo
dotnet run -- --Database:ConnectionString="Host=localhost;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner"
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

RunnerRunner releases are HostSilo-only: there is no legacy SignalR agent package.

### Linux server and bundled Linux HostSilo

```bash
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/install-server.sh -o install-server.sh
chmod +x install-server.sh
sudo ./install-server.sh --orleans-ip 192.168.2.4 --host-silo-ip 192.168.2.4
```

Updates are intentionally one command:

```bash
sudo runnerrunner-server update
```

The release compose bundle consumes published GHCR images and runs PostgreSQL, `RunnerRunner.Server`, and a Linux `RunnerRunner.HostSilo`.

### Linux HostSilo

Linux Docker-backed hosts should run the HostSilo container with the host Docker socket mounted. The bundled Linux compose stack already does this. Native `linux-x64` and `linux-arm64` HostSilo tarballs are also published for hosts that need native process execution.

### macOS HostSilo

macOS uses a native LaunchAgent, not Docker. Docker on macOS runs Linux containers inside a VM and cannot reliably control Tart, Xcode, Keychain, or native macOS runner processes on the host.

```bash
curl -fsSL https://github.com/redth/RunnerRunner/releases/latest/download/install-host-macos.sh -o install-host-macos.sh
chmod +x install-host-macos.sh
./install-host-macos.sh \
  --host-name mac-mini-01 \
  --advertised-ip 192.168.2.134 \
  --database-connection 'Host=192.168.2.4;Port=5433;Database=runnerrunner;Username=runnerrunner;Password=...'
```

### Windows HostSilo

Windows uses a native Windows Service package by default. Windows Docker mode is not the main release path.

```powershell
Expand-Archive .\runnerrunner-hostsilo-win-x64.zip -DestinationPath 'C:\Program Files\RunnerRunner' -Force
powershell -ExecutionPolicy Bypass -File .\Install-HostSilo.ps1 `
  -HostName windows-build-01 `
  -AdvertisedIPAddress 192.168.2.50 `
  -DatabaseConnectionString 'Host=192.168.2.4;Port=5433;Database=runnerrunner;Username=runnerrunner;Password=...'
```

## Docker Compose Topology

The checked-in `docker-compose.yml` runs the production-like local topology:

| Service | Purpose |
|---|---|
| `postgres` | PostgreSQL 17 for DocumentDB, Orleans clustering, grain state, and reminders |
| `server` | Blazor UI, webhook API, Orleans server silo |
| `host-silo` | Headless Orleans host silo with local runner execution |

Key ports:

| Port | Service |
|---|---|
| `4779` | RunnerRunner web UI and API |
| `5432` | PostgreSQL |
| `11111` / `30000` | Server Orleans silo/gateway |
| `11112` / `30001` | HostSilo Orleans silo/gateway |

The compose file wires both server and host-silo to:

```text
Database__ConnectionString=Host=postgres;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner
```

> **Note:** macOS agents cannot run in Docker. Deploy the macOS agent or HostSilo as a native binary and point it at the server URL.

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
| `Orleans:AdvertisedIPAddress` | *(empty)* | External IP advertised to other Orleans silos in production |

### HostSilo

| Setting | Default | Description |
|---|---|---|
| `Database:ConnectionString` | *(empty)* | PostgreSQL connection string for Orleans clustering and runner state |
| `RunnerRunner:HostId` | Machine hostname | Stable host identity |
| `RunnerRunner:HostName` | Machine hostname | Display name for this host |
| `RunnerRunner:Platform` | Current OS | Optional platform override reported to the server |
| `RunnerRunner:Architecture` | Current process architecture | Optional architecture override reported to the server |
| `Orleans:AdvertisedIPAddress` | *(empty)* | External IP advertised to other Orleans silos in production |

All settings can be provided via:
- `appsettings.json`
- Environment variables (e.g. `RunnerRunner__HostName`)
- Command-line args (e.g. `--RunnerRunner:HostName=mac-mini-01`)

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
├── RunnerRunner.HostSilo/          # Per-host Orleans silo execution path
└── RunnerRunner.Agent/             # Reusable runner backend/lifecycle library

tests/
├── RunnerRunner.Core.Tests/
├── RunnerRunner.Server.Tests/
└── RunnerRunner.Agent.Tests/
```

## License

MIT
