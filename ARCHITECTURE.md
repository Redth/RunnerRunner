# RunnerRunner Architecture

RunnerRunner is a self-hosted CI/CD runner orchestration platform that manages GitHub Actions, Gitea Actions, and Azure DevOps runners across heterogeneous machines (macOS, Linux, Windows) from a single web UI.

## System Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                     RunnerRunner Server                         │
│                                                                 │
│  ┌──────────┐  ┌──────────────┐  ┌───────────────────────────┐ │
│  │ Blazor   │  │  SignalR Hub  │  │     Orleans Silo          │ │
│  │ Web UI   │  │  (AgentHub)   │  │  ┌─────────────────────┐  │ │
│  │          │  │               │  │  │ HostGrain            │  │ │
│  │ 12 pages │  │ Agent events  │  │  │ RunnerInstanceGrain  │  │ │
│  │          │◄─┤ → grain calls │  │  │ ProfileGrain         │  │ │
│  │          │  │               │  │  │ ProvisioningRuleGrain│  │ │
│  │          │  │ Commands ←    │  │  │ SchedulerGrain       │  │ │
│  │          │  │   from grains │  │  │ WebhookProcessorGrain│  │ │
│  └──────────┘  └───────┬──────┘  │  │ HostGroupGrain       │  │ │
│                        │         │  └─────────────────────┘  │ │
│  ┌──────────────────┐  │         │  ┌─────────────────────┐  │ │
│  │ Shiny DocumentDB │  │         │  │ Orleans Streams     │  │ │
│  │ (SQLite)         │  │         │  │ → real-time UI      │  │ │
│  └──────────────────┘  │         │  └─────────────────────┘  │ │
│                        │         └───────────────────────────┘ │
│  ┌──────────────────┐  │                                       │
│  │ Webhook Endpoints│  │    POST /api/webhooks/github          │
│  │ (ASP.NET)        │──┤    POST /api/webhooks/gitea           │
│  └──────────────────┘  │                                       │
└────────────────────────┼───────────────────────────────────────┘
                         │ SignalR WebSocket
            ┌────────────┼────────────┐
            │            │            │
   ┌────────▼──┐  ┌──────▼───┐  ┌────▼───────┐
   │  Agent    │  │  Agent   │  │  Agent     │
   │  (Linux)  │  │  (macOS) │  │  (Windows) │
   │           │  │          │  │            │
   │ ┌───────┐ │  │ ┌──────┐ │  │ ┌────────┐ │
   │ │Docker │ │  │ │Tart  │ │  │ │Native  │ │
   │ │Backend│ │  │ │Backend│ │  │ │Backend │ │
   │ ├───────┤ │  │ ├──────┤ │  │ └────────┘ │
   │ │Native │ │  │ │Native│ │  │            │
   │ │Backend│ │  │ │Backend│ │  │            │
   │ └───────┘ │  │ └──────┘ │  │            │
   └───────────┘  └──────────┘  └────────────┘
```

## Projects

| Project | Purpose |
|---------|---------|
| **RunnerRunner.Core** | Shared domain models, hub contracts, interfaces |
| **RunnerRunner.Server** | Blazor web UI, Orleans silo, SignalR hub, webhook endpoints |
| **RunnerRunner.Agent** | Worker service deployed on each host, manages runner lifecycles |
| **RunnerRunner.AppHost** | .NET Aspire orchestrator for local development |
| **RunnerRunner.ServiceDefaults** | OpenTelemetry, health checks, service discovery |

## Domain Model

### Core Concepts

- **Runner Profile** ("WHAT") — Defines the runner configuration: which CI provider (GitHub/Gitea/AzDO), execution backend (Docker/Tart/Native), container/VM images, environment variables, labels.

- **Provisioning Rule** ("HOW") — Defines how profiles get provisioned. Unified model supporting:
  - **Static** — Maintain a fixed number of runner instances
  - **ScaleSet** — Auto-scale between `MinReady` (warm pool) and `MaxInstances`
  - **Webhook** — React to GitHub/Gitea `workflow_job` events with JIT provisioning. Can also maintain a warm pool.
  - **Scheduled** — Cron-based provisioning (future)

- **Host** — A physical or virtual machine that runs runner instances. Declares capabilities via labels (`os=linux`, `arch=x64`, `docker=true`, `pool=build-farm`) and resource limits.

- **Host Group** — Logical grouping of hosts with shared labels. For organizational convenience and targeting.

- **Runner Instance** — An actual running (or stopped/failed) runner with full lifecycle tracking.

### Key Models (`RunnerRunner.Core/Models/`)

| Model | Description |
|-------|-------------|
| `RunnerProfile` | Runner config: provider, backend, images, env vars, labels |
| `ProvisioningRule` | Unified provisioning trigger (Static/ScaleSet/Webhook/Scheduled) |
| `Host` | Machine with labels, capabilities, resource limits |
| `RunnerInstance` | Lifecycle-tracked runner (status, timestamps, container/VM/process IDs) |
| `ProviderCredential` | GitHub/Gitea/AzDO authentication credentials |
| `EnvironmentVariableSet` | Reusable env var collections composable into profiles |
| `WebhookBinding` | Legacy webhook config (being replaced by ProvisioningRule) |
| `RunnerAssignment` | Legacy static assignment (being replaced by ProvisioningRule) |
| `WebhookEvent` | Audit log for received webhook events |

## Orleans Grain Architecture

The server uses Microsoft Orleans for stateful, resilient management of all entities. Each grain owns its lifecycle, timers, and state persistence.

### Grains

| Grain | Key Type | Responsibilities |
|-------|----------|-----------------|
| **HostGrain** | String (host ID) | Labels, resource tracking, agent connection state, heartbeat timeout timer (90s) |
| **HostGroupGrain** | String (group ID) | Member host list, shared labels |
| **RunnerInstanceGrain** | String (instance ID) | Lifecycle state machine with 5 grain timers for timeouts |
| **ProfileGrain** | String (profile ID) | Config storage, environment variable composition with caching |
| **ProvisioningRuleGrain** | String (rule ID) | Reconciliation loop (30s), warm pool management, webhook event routing |
| **SchedulerGrain** | Integer (singleton=0) | Label-based host selection with capacity checking and load balancing |
| **WebhookProcessorGrain** | Integer (StatelessWorker×4) | Parallel webhook HMAC validation, event parsing, routing |

### Runner Instance Lifecycle (State Machine)

```
Pending ──► Starting ──► Running ──► Stopping ──► Stopped
   │            │           │            │
   │            │           │            └──► Failed (stop timeout 5m)
   │            │           │
   │            │           ├──► Failed (dynamic timeout 2h)
   │            │           └──► Crashed (health stale 3m)
   │            │
   │            └──► Failed (registration timeout 5m)
   │
   └──► Failed (deploy timeout 2m)
```

Each state transition is a grain method — atomic, no race conditions. Timeouts are grain timers that auto-fire.

### Orleans Streams

Grains publish state changes to Orleans memory streams:
- `RunnerStatusChangedEvent` — published by RunnerInstanceGrain on every status transition
- `HostStatusChangedEvent` — published by HostGrain on online/offline changes

`StreamSubscriptionService` bridges these to Blazor static events for real-time UI updates without polling.

## Agent Architecture

Each host runs a lightweight agent (`RunnerRunner.Agent`) that:

1. **Connects** to the server via SignalR WebSocket with auto-reconnect
2. **Receives commands**: DeployRunner, StopRunner, CleanupOrphan, etc.
3. **Reports events**: RunnerStarted, RunnerStopped, Heartbeat, Reconciliation
4. **Manages backends**: Docker, Tart, Native — each implementing `IRunnerBackend`

### Execution Backends

| Backend | Platform | How it works |
|---------|----------|-------------|
| **Docker** | Linux (primary) | `docker run` with labels for tracking. JIT: inspects image, overrides entrypoint to use `--jitconfig` with shell detection |
| **Tart** | macOS | Clones VM image, starts VM, SSHs in to download + configure runner. Scripts piped via stdin to avoid quoting issues |
| **Native** | Any | Downloads runner binary, runs as a process. Writes `rr.pid` for tracking. Output piped to `runner.log` |

### Reconciliation

Every 30 seconds (in the heartbeat loop), the agent:
1. Discovers all managed resources (Docker labels, Tart `rr-` prefix VMs, Native PID files)
2. Sends a `ReconciliationReport` to the server
3. Server compares against DB — marks stale records as Stopped/Crashed, sends CleanupOrphan for ghosts

## Webhook / JIT Provisioning

```
GitHub/Gitea ──POST──► /api/webhooks/{provider}
                              │
                    1. HMAC-SHA256 signature validation
                    2. Match binding by repo/org + signature
                    3. If action=queued:
                       a. Label matching → find profile
                       b. JIT config via GitHub/Gitea API
                       c. Select host (labels + capacity)
                       d. Deploy runner to agent
                    4. If action=in_progress:
                       → Confirm runner is executing
                    5. If action=completed:
                       → Stop runner, delete container/VM, remove record
```

### JIT Runner Flow by Backend

- **Native**: `run.sh --jitconfig <base64>` — skips config.sh entirely
- **Docker**: JIT config written to env var `RR_JIT_CONFIG`, entrypoint overridden to find and use `run.sh --jitconfig`
- **Tart**: JIT config written to temp file in VM via SSH stdin, runner reads and starts with `--jitconfig`

## Environment Variable Composition

Variables are composed in 5 layers (later layers override earlier):

1. **RR_\* auto-injected** from provider credentials (RR_GITHUB_TOKEN, etc.)
2. **Environment Variable Sets** (ordered by priority in the profile)
3. **Profile-level overrides**
4. **Host-level overrides**
5. **Instance-level** (RR_INSTANCE_ID, RR_RUNNER_NAME, RR_BASE_PATH, RR_WORK_DIR)

Then `$VAR` / `${VAR}` expansion runs (3-pass chaining).

## Web UI

Blazor Server with InteractiveServer render mode. 12 pages:

| Page | Route | Purpose |
|------|-------|---------|
| Dashboard | `/` | Overview cards, connected agents table |
| Hosts | `/hosts` | Host management, labels, resource limits, assignments |
| Runners | `/runners` | Runner instances grouped by host, lifecycle status |
| Profiles | `/profiles` | Runner profile CRUD with env var composition |
| Provisioning Rules | `/provisioning-rules` | Unified Static/ScaleSet/Webhook/Scheduled rules |
| Images | `/images` | Docker/Tart image management per host |
| Logs | `/logs` | xterm.js terminal with agent/runner log viewing |
| Env Variables | `/envvarsets` | Reusable environment variable set CRUD |
| Webhooks | `/webhooks` | Webhook binding CRUD (legacy, being unified) |
| Webhook Events | `/webhook-events` | Audit log of received webhook events |
| Settings | `/settings` | Provider credentials, registry credentials |

### UI Features

- **Light/dark theme** with localStorage persistence
- **Collapsible sidebar** (persisted state)
- **Resizable table columns** and split panes
- **Mobile responsive** with hamburger menu overlay
- **Platform chips** with icons (Linux/macOS/Windows)
- **xterm.js** terminal for log viewing with ANSI color support
- **Real-time updates** via Orleans streams (no polling)

## Deployment

### Linux (Docker Compose)

```bash
./deploy/deploy-all.sh linux
```

Deploys server + agent containers to a remote Linux host via SSH. NPM proxy labels for reverse proxy.

### macOS (Native Agent)

```bash
./deploy/deploy-all.sh macos
```

Publishes self-contained binary, SCPs to host, codesigns, starts via `nohup`.

### Both

```bash
./deploy/deploy-all.sh all
```

## Data Storage

- **Shiny DocumentDB (SQLite)** — Primary data store for all models. Used by both legacy services and Orleans grains (dual-write during migration).
- **Orleans Grain State** — In-memory storage (dev). SQLite ADO.NET packages installed for future production persistence.
- **Orleans Streams** — In-memory streams for real-time UI events.

## Migration Status

The codebase is in a dual-write migration from direct DocumentDB services to Orleans grains:

| Component | Legacy (DocumentDB) | Orleans Grain | Status |
|-----------|---------------------|---------------|--------|
| Host state | AgentHub static dict | HostGrain | Dual-write |
| Runner lifecycle | RunnerTimeoutService | RunnerInstanceGrain | **Grain active** |
| Reconciliation | ReconciliationService | HostGrain heartbeat | **Grain active** |
| Static provisioning | OrchestrationEngine | ProvisioningRuleGrain | Legacy active |
| Dynamic provisioning | DynamicProvisioningService | ProvisioningRuleGrain | Legacy active |
| Host selection | Inline in services | SchedulerGrain | Available |
| Webhook processing | WebhookEndpoints | WebhookProcessorGrain | Legacy active |
| UI reads | DocumentDB queries | IGrainFactory injected | Prepared |
