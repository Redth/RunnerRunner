# 🏃 RunnerRunner

A self-hosted CI/CD runner orchestration platform for managing GitHub Actions, Gitea Actions, and Azure DevOps runners across a fleet of heterogeneous machines (macOS, Linux, Windows) from a single web UI.

## What It Does

RunnerRunner lets you:

- **Declare runner profiles** — pick a CI provider, execution backend, env vars, image config, and labels
- **Assign profiles to hosts** — say "run 3 instances of this profile on that machine" and the system converges
- **Compose environment variables** — build reusable env var sets (e.g. `dotnet-8-sdk`, `xcode-15-env`) and layer them with priority
- **Auto-register/deregister runners** — runners register with GitHub/Gitea/AzDO on startup, deregister on shutdown
- **Orchestrate multiple hosts** — agents connect to the central server from macOS, Linux, or Windows machines

## Architecture

```
┌──────────────────────────────────────────┐
│          RunnerRunner.Server             │
│   Blazor Web UI + SignalR Hub + SQLite   │
└──────────────┬───────────────────────────┘
               │ SignalR (WebSocket)
    ┌──────────┼──────────────────┐
    │          │                  │
┌───▼────┐ ┌──▼─────┐  ┌────────▼──┐
│ Agent  │ │ Agent  │  │  Agent    │
│ macOS  │ │ Linux  │  │  Windows  │
└───┬────┘ └──┬─────┘  └────┬─────┘
    │         │              │
 Tart VMs  Docker         Docker /
 / Native  Containers     Native
```

**Server** — Blazor web UI for managing profiles, hosts, env vars, credentials. Runs the desired-state reconciliation engine that auto-scales runners via SignalR commands.

**Agent** — Lightweight worker deployed on each host machine. Connects outbound to the server (no inbound ports needed). Executes runner lifecycle using Docker, Tart VMs, or native processes.

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
- **Agent** auto-connected to the server
- **Aspire Dashboard** with real-time logs, traces, and metrics for all services

### Option 2: Deploy Everything (One Command)

Deploy the full stack to your Linux host + macOS agent in one shot:

```bash
./deploy/deploy-all.sh
```

This will:
1. Build Docker images for Server + Linux Agent
2. Push to your container registry (default: `ghcr.io/redth/runnerrunner`)
3. SSH into the Linux host, deploy via `docker compose up`
4. Publish a native `osx-arm64` binary, SSH into the Mac, install as a launchd service

**Configure hosts** via environment variables:

```bash
LINUX_HOST=192.168.2.2 \
MACOS_HOST=192.168.2.134 \
./deploy/deploy-all.sh
```

All defaults are in the script — edit `deploy/deploy-all.sh` or override with env vars.

**Prerequisites:** `docker login ghcr.io` locally, Docker on the Linux host, SSH access to both hosts.

### Option 2: Deploy to Remote Linux Host (via SSH)

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

### Option 3: Docker Compose (Production / Standalone)

```bash
docker compose up -d
```

The server is available at `http://localhost:8080`.

### Option 3: Manual (dotnet run)

```bash
# Terminal 1: Start the server
cd src/RunnerRunner.Server
dotnet run

# Terminal 2: Start a local agent
cd src/RunnerRunner.Agent
dotnet run -- --RunnerRunner:ServerUrl=http://localhost:5080 --RunnerRunner:AgentName=my-local-agent
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

## Deploying Agents

### Linux (Docker)

```bash
docker run -d \
  --name runnerrunner-agent \
  -e RunnerRunner__ServerUrl=https://your-server:8080 \
  -e RunnerRunner__AgentName=linux-build-01 \
  -e RunnerRunner__AgentToken=your-enrollment-token \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --restart unless-stopped \
  ghcr.io/your-org/runnerrunner-agent:latest
```

### macOS (Native Binary via Deploy Script — Recommended)

The deploy script publishes, copies, and installs the agent as a launchd service in one command:

```bash
# 1. Configure (one-time): copy and edit the env file
cp deploy/macos/agent.env.example deploy/macos/agent.env
# Edit deploy/macos/agent.env with your server URL, agent name, token

# 2. Deploy to your Mac
./deploy/macos/deploy-agent.sh 192.168.2.134

# Or with a different SSH user
SSH_USER=admin ./deploy/macos/deploy-agent.sh 192.168.2.134
```

This installs the agent to `/opt/runnerrunner/` and registers it as a launchd system service that:
- Starts automatically on boot
- Restarts automatically if it crashes
- Logs to `/var/log/runnerrunner-agent.log`

**Re-deploy after code changes** — just run the same command again:
```bash
./deploy/macos/deploy-agent.sh 192.168.2.134
```

**Service management on the Mac:**
```bash
# Check status
ssh root@192.168.2.134 'launchctl print system/com.runnerrunner.agent'

# View logs
ssh root@192.168.2.134 'tail -f /var/log/runnerrunner-agent.log'

# Restart
ssh root@192.168.2.134 'launchctl kickstart -k system/com.runnerrunner.agent'

# Stop
ssh root@192.168.2.134 'launchctl bootout system/com.runnerrunner.agent'
```

> **Prerequisites on the Mac host:** SSH access, and optionally [tart](https://tart.run) (`brew install cirruslabs/cli/tart`) for macOS VM runners.

### Windows (Docker or Native)

```powershell
# Docker
docker run -d `
  -e RunnerRunner__ServerUrl=https://your-server:8080 `
  -e RunnerRunner__AgentName=windows-build-01 `
  -e RunnerRunner__AgentToken=your-enrollment-token `
  ghcr.io/your-org/runnerrunner-agent:latest

# Or native
dotnet run --project src/RunnerRunner.Agent -- `
  --RunnerRunner:ServerUrl=https://your-server:8080 `
  --RunnerRunner:AgentName=windows-build-01
```

## Docker Compose Examples

### Basic: Server + Local Agent

```yaml
services:
  server:
    build:
      context: .
      dockerfile: src/RunnerRunner.Server/Dockerfile
    ports:
      - "8080:8080"
    volumes:
      - server-data:/app/data
    environment:
      - Database__Path=/app/data/runnerrunner.db
      - ASPNETCORE_URLS=http://+:8080

  agent:
    build:
      context: .
      dockerfile: src/RunnerRunner.Agent/Dockerfile
    environment:
      - RunnerRunner__ServerUrl=http://server:8080
      - RunnerRunner__AgentName=local-docker-host
      - RunnerRunner__AgentToken=changeme
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    depends_on:
      - server

volumes:
  server-data:
```

### Production: Server Behind Reverse Proxy

```yaml
services:
  server:
    build:
      context: .
      dockerfile: src/RunnerRunner.Server/Dockerfile
    expose:
      - "8080"
    volumes:
      - server-data:/app/data
    environment:
      - Database__Path=/app/data/runnerrunner.db
      - ASPNETCORE_URLS=http://+:8080
      - ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
    restart: unless-stopped

  nginx:
    image: nginx:alpine
    ports:
      - "443:443"
      - "80:80"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
      - ./certs:/etc/nginx/certs:ro
    depends_on:
      - server
    restart: unless-stopped

volumes:
  server-data:
```

### Multi-Agent: Server + Multiple Hosts

```yaml
services:
  server:
    build:
      context: .
      dockerfile: src/RunnerRunner.Server/Dockerfile
    ports:
      - "8080:8080"
    volumes:
      - server-data:/app/data
    environment:
      - Database__Path=/app/data/runnerrunner.db
      - ASPNETCORE_URLS=http://+:8080

  agent-linux-1:
    build:
      context: .
      dockerfile: src/RunnerRunner.Agent/Dockerfile
    environment:
      - RunnerRunner__ServerUrl=http://server:8080
      - RunnerRunner__AgentName=linux-build-01
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    depends_on:
      - server

  agent-linux-2:
    build:
      context: .
      dockerfile: src/RunnerRunner.Agent/Dockerfile
    environment:
      - RunnerRunner__ServerUrl=http://server:8080
      - RunnerRunner__AgentName=linux-build-02
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    depends_on:
      - server

volumes:
  server-data:
```

> **Note:** macOS agents can't run in Docker — deploy the agent as a native binary on Mac hosts and point them at the server URL.

## Execution Backends

| Backend | Platform | How It Works |
|---|---|---|
| **Docker** | Linux, Windows | Spins up a container per runner instance. Image provides the runner environment. Registration happens in the container's entrypoint. |
| **Tart** | macOS | Clones an OCI VM image, configures via shared directory `.env` file, starts the VM. Used for macOS CI (Xcode builds, iOS testing). |
| **Native** | Any | Runs the runner agent binary directly as a child process. Multiple runners share the host OS. Best for bare-metal macOS. |

## Environment Variable Composition

Env vars are composed in layers (later layers win on key conflicts):

```
Layer 1: Env Var Sets (ordered by priority — higher wins)
   └─ dotnet-8-sdk (priority 1): DOTNET_ROOT=/usr/share/dotnet
   └─ android-sdk (priority 2): ANDROID_HOME=/opt/android

Layer 2: Profile Overrides
   └─ DOTNET_ROOT=/custom/dotnet  (overrides set value)

Layer 3: Host Overrides
   └─ ANDROID_HOME=/local/android  (overrides everything for this host)

Layer 4: Instance (auto-injected)
   └─ RUNNER_NAME=profile-abc123
```

## Configuration Reference

### Server

| Setting | Default | Description |
|---|---|---|
| `Database:Path` | `runnerrunner.db` | Path to the SQLite database file |
| `ASPNETCORE_URLS` | `http://localhost:5000` | Server listen URL |

### Agent

| Setting | Default | Description |
|---|---|---|
| `RunnerRunner:ServerUrl` | `http://localhost:8080` | URL of the RunnerRunner server |
| `RunnerRunner:AgentName` | Machine hostname | Display name for this agent |
| `RunnerRunner:AgentToken` | *(empty)* | Enrollment token (generated in server UI) |
| `RunnerRunner:AgentId` | *(auto-generated)* | Unique agent identifier |

All settings can be provided via:
- `appsettings.json`
- Environment variables (e.g. `RunnerRunner__ServerUrl`)
- Command-line args (e.g. `--RunnerRunner:ServerUrl=...`)

## Development

### Build

```bash
dotnet build
```

### Test

```bash
dotnet test
```

55 tests across 3 projects:
- **Core.Tests** — Model defaults, Id generation
- **Server.Tests** — Provider API tests (mocked HTTP), orchestration engine logic, DocumentStore integration
- **Agent.Tests** — Lifecycle manager, health reporter

### Project Structure

```
src/
├── RunnerRunner.AppHost/           # Aspire orchestrator (local dev)
├── RunnerRunner.ServiceDefaults/   # Shared OTEL, health checks, service discovery
├── RunnerRunner.Core/              # Domain models, interfaces, SignalR contracts
├── RunnerRunner.Server/            # Blazor web UI + orchestration engine
└── RunnerRunner.Agent/             # Remote agent (deploys on each host)

tests/
├── RunnerRunner.Core.Tests/
├── RunnerRunner.Server.Tests/
└── RunnerRunner.Agent.Tests/
```

## License

MIT
