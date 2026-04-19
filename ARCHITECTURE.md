# RunnerRunner Architecture

RunnerRunner is a self-hosted CI/CD runner orchestration platform that manages GitHub Actions, Gitea Actions, and Azure DevOps runners across heterogeneous machines (macOS, Linux, Windows) from a single web UI.

## System Overview

```
                          +----------------+
                          |  PostgreSQL 17 |
                          |  (shared DB)   |
                          +-------+--------+
                                  |
              +-------------------+-------------------+
              |                   |                   |
   +----------v-----------+  +---v-----------+  +---v-----------+
   |    Server Silo        |  |  Host Silo    |  |  Host Silo    |
   |                       |  |  (Linux)      |  |  (macOS)      |
   | +-------------------+ |  |               |  |               |
   | | Blazor Web UI     | |  | +-----------+ |  | +-----------+ |
   | | (14 pages)        | |  | | Docker    | |  | | Tart      | |
   | +-------------------+ |  | | Backend   | |  | | Backend   | |
   | | Webhook API       | |  | +-----------+ |  | +-----------+ |
   | | Orleans Dashboard | |  | | Native    | |  | | Native    | |
   | +-------------------+ |  | | Backend   | |  | | Backend   | |
   | | Orleans Grains    | |  | +-----------+ |  | +-----------+ |
   | | SchedulerGrain    | |  |               |  |               |
   | | WebhookProcessor  | |  | Orleans Silo  |  | Orleans Silo  |
   | | ProfileGrain      | |  | (cluster      |  | (cluster      |
   | | ProvisioningRule  | |  |  member)      |  |  member)      |
   | +-------------------+ |  +---------+-----+  +------+--------+
   |                       |            |                |
   | +-------------------+ |     +------v--------+      |
   | | SignalR Hub       |<+-----| Legacy Agent  |      |
   | | (AgentHub)        | |     | (migration)   |      |
   | +-------------------+ |     +---------------+      |
   |                       |                             |
   | Shiny DocumentDB     |                             |
   | (PostgreSQL)         |                             |
   +----------+------------+                             |
              |                                          |
              +------------------------------------------+
              All silos join one Orleans cluster via PostgreSQL
```

## Projects

| Project | Purpose |
|---------|---------|
| **RunnerRunner.Core** | Shared domain models, hub contracts, interfaces |
| **RunnerRunner.Server** | Blazor web UI, Orleans silo, SignalR hub, webhook endpoints, Orleans Dashboard |
| **RunnerRunner.HostSilo** | Headless Orleans silo deployed on each host machine (future replacement for Agent) |
| **RunnerRunner.Agent** | Legacy worker service on each host (SignalR client, being replaced by HostSilo) |
| **RunnerRunner.AppHost** | .NET Aspire orchestrator for local development |
| **RunnerRunner.ServiceDefaults** | OpenTelemetry, health checks, service discovery |

## Domain Model

### Core Concepts

- **Runner Profile** ("WHAT") — Defines the runner configuration: CI provider (GitHub/Gitea/AzDO), execution backend (Docker/Tart/Native), container/VM images, environment variables, labels.

- **Provisioning Rule** ("HOW") — Unified model defining how profiles get provisioned:
  - **Static** — Maintain a fixed number of runner instances
  - **ScaleSet** — Auto-scale between `MinReady` (warm pool) and `MaxInstances` with cooldown
  - **Webhook** — React to GitHub/Gitea `workflow_job` events with JIT provisioning. Can also maintain a warm pool.
  - **Scheduled** — Cron-based provisioning (future)

- **Host** — A physical or virtual machine. Declares capabilities via labels (`os=linux`, `arch=x64`, `docker=true`, `pool=build-farm`) and resource limits (max Docker containers, Tart VMs, native processes).

- **Host Group** — Logical grouping of hosts with shared labels for organizational convenience.

- **Runner Instance** — An actual running/stopped runner with full lifecycle tracking, timestamps, and status messages.

### Key Models (`RunnerRunner.Core/Models/`)

| Model | Description |
|-------|-------------|
| `RunnerProfile` | Runner config: provider, backend, images, env vars, labels |
| `ProvisioningRule` | Unified provisioning (Static/ScaleSet/Webhook/Scheduled) |
| `Host` | Machine with labels, capabilities, resource limits, group membership |
| `RunnerInstance` | Lifecycle-tracked runner with DeployedAt, StatusMessage, health tracking, and StatusHistory timeline |
| `StatusHistoryEntry` | Records a single status transition: timestamp, status, message, source (grain_call/timer/webhook/health_check) |
| `ProviderCredential` | GitHub/Gitea/AzDO authentication credentials |
| `EnvironmentVariableSet` | Reusable env var collections composable into profiles |
| `WebhookEvent` | Audit log for received webhook events |

## Orleans Architecture

RunnerRunner uses Microsoft Orleans 10.0.1 for resilient, distributed state management. All silos form a single cluster connected via PostgreSQL.

### Cluster Topology

- **Server Silo** — Co-hosted with Blazor. Runs UI-facing grains and webhook processing.
- **Host Silos** — One per physical host. Declares silo metadata (hostId, platform, architecture). Will run HostGrain and RunnerInstanceGrain locally for direct backend execution.
- All silos share PostgreSQL for clustering, grain persistence, and reminders.

### Grains (7 types)

| Grain | Key Type | Responsibilities |
|-------|----------|-----------------|
| **HostGrain** | String (host ID) | Labels, resource tracking, heartbeat timeout (90s), DocumentDB sync |
| **HostGroupGrain** | String (group ID) | Member host list, shared labels |
| **RunnerInstanceGrain** | String (instance ID) | Lifecycle state machine with 5 grain timers, DocumentDB sync, stream publishing |
| **ProfileGrain** | String (profile ID) | Config storage, env var composition with caching |
| **ProvisioningRuleGrain** | String (rule ID) | Reconciliation (30s timer), warm pool management, webhook routing |
| **SchedulerGrain** | Integer (singleton=0) | Label-based host selection, capacity checking, load balancing |
| **WebhookProcessorGrain** | Integer (StatelessWorker x4) | Parallel webhook HMAC validation, event parsing, routing |

### Runner Instance Lifecycle

```
Pending --> Starting --> Running --> Stopping --> Stopped
   |            |           |            |
   |            |           |            +--> Failed (stop timeout 5m)
   |            |           |
   |            |           +--> Failed (dynamic timeout 2h)
   |            |           +--> Crashed (health stale 3m)
   |            |
   |            +--> Failed (registration timeout 5m)
   |
   +--> Failed (deploy timeout 2m)
```

Each transition is a grain method (atomic). Timeouts are grain timers.

### Dual-Write Architecture

Grains maintain two views of state:
1. **Orleans grain state** (PostgreSQL ADO.NET) — source of truth for lifecycle, timers, activation
2. **Shiny DocumentDB** (PostgreSQL) — queryable read projection for UI LINQ queries

Grains call `SyncToDocumentDb()` after state changes. UI reads from DocumentDB for fast queries; grain methods handle lifecycle operations.

### Orleans Streams

- `RunnerStatusChangedEvent` — published by RunnerInstanceGrain on status transitions
- `HostStatusChangedEvent` — published by HostGrain on online/offline changes
- `StreamSubscriptionService` bridges streams to Blazor for auto-refresh (Dashboard, Runners pages)

### Observability

- **Orleans Dashboard** — Built-in at `/orleans`. Grain stats, silo health, call chains.
- **OpenTelemetry** — Activity propagation across grain calls. OTLP export configurable via `OTEL_EXPORTER_OTLP_ENDPOINT`.

## Execution Backends

| Backend | Platform | How it works |
|---------|----------|-------------|
| **Docker** | Linux | `docker run` with labels. JIT: inspects image entrypoint, overrides with `--jitconfig` using detected shell |
| **Tart** | macOS | Clones VM, SSHs in (via `sshpass` + stdin piping), downloads runner, configures/JIT |
| **Native** | Any | Downloads runner binary, runs as process. `rr.pid` for tracking, output to `runner.log` |

## Webhook / JIT Provisioning

```
GitHub/Gitea --> POST /api/webhooks/{provider}
                       |
             1. HMAC-SHA256 signature validation
             2. Match binding by signature + scope
             3. queued    --> label match --> JIT config --> deploy
             4. in_progress --> confirm runner executing
             5. completed --> stop + cleanup + remove record
```

JIT config generated via GitHub `generate-jitconfig` API or Gitea registration token. Tries repo-level first, then org-level.

## Environment Variable Composition

5 layers (later overrides earlier):
1. RR_* auto-injected from provider credentials
2. Environment Variable Sets (ordered by priority)
3. Profile-level overrides
4. Host-level overrides
5. Instance-level (RR_INSTANCE_ID, RR_RUNNER_NAME, etc.)

Then `$VAR` / `${VAR}` expansion runs (3-pass chaining).

## Capacity and Limit Roll-up

RunnerRunner applies concurrency and capacity limits in a fixed order so operators can predict why work is waiting:

1. **FIFO queue fairness** — older queued webhook jobs in the same provisioning lane keep newer jobs from jumping ahead.
2. **Provisioning rule capacity** — `MaxConcurrent` for webhook rules, `MaxInstances` for scale sets, and `DesiredCount` for static rules define the rule-level ceiling.
3. **Runner profile capacity** — `RunnerProfile.MaxParallelPerHost` limits how many runners of that profile can run on any one host at the same time.
4. **Host backend capacity** — `Host.MaxDockerContainers`, `Host.MaxTartVMs`, and `Host.MaxNativeProcesses` are the final per-host backend slot limits.

The **effective available capacity** for a queued job is the smallest remaining slot count across all applicable layers after host matching is complete. In practice, a rule might allow 10 concurrent runners, but if the mapped profile is capped at 1 per host and only 3 matching hosts are available, the real ceiling is 3 until the host pool grows.

Operational notes:

- A host backend limit of **0** means that backend is disabled on that host.
- The web UI now surfaces the current blocker as **FIFO**, **Rule**, **Profile**, or **Host** on the Events, Provisioning Rules, Hosts, Runners, and Profiles pages.

## Web UI (15 pages)

| Page | Route | Purpose |
|------|-------|---------|
| Dashboard | `/` | Overview cards, cluster status, connected agents |
| Hosts | `/hosts` | Host management, labels, resource limits |
| Runners | `/runners` | Runner instances grouped by host, auto-refresh via streams |
| Jobs | `/jobs` | Job-centric view with lifecycle history, provisioning context, and webhook details |
| Profiles | `/profiles` | Runner profile CRUD with env var composition |
| Provisioning Rules | `/provisioning-rules` | Unified Static/ScaleSet/Webhook/Scheduled rules |
| Images | `/images` | Docker/Tart image management per host (split pane) |
| Logs | `/logs` | xterm.js terminal with agent/runner log viewing (split pane) |
| Env Variables | `/envvarsets` | Reusable environment variable sets |
| Webhooks | `/webhooks` | Webhook bindings (legacy, see Provisioning Rules) |
| Webhook Events | `/webhook-events` | Audit log of received webhook events |
| Settings | `/settings` | Provider credentials, registry credentials |
| Orleans Dashboard | `/orleans` | Built-in Orleans grain/silo monitoring |

### UI Features

- Light/dark theme with localStorage persistence
- Collapsible sidebar (persisted state)
- Resizable table columns and split panes
- Mobile responsive with hamburger menu overlay
- Platform chips with icons (Linux/macOS/Windows)
- xterm.js terminal with ANSI color support and search
- Real-time updates via Orleans streams (no polling)
- Confirmation dialogs on all destructive operations
- Orphaned runner detection and cleanup

## Data Storage

Single PostgreSQL 17 instance:

| Layer | Provider | Purpose |
|-------|----------|---------|
| Orleans Clustering | ADO.NET (Npgsql) | Silo membership, failure detection |
| Orleans Grain State | ADO.NET (Npgsql) | Persistent grain state |
| Orleans Reminders | ADO.NET (Npgsql) | Durable reminder scheduling |
| Orleans Streams | In-memory | Real-time UI events |
| Shiny DocumentDB | PostgreSQL | Queryable read projections for UI |

## Deployment

### Linux (Docker Compose)

```bash
./deploy/deploy-all.sh linux
```

4 containers: `postgres`, `server` (Blazor + Orleans silo), `host-silo` (headless Orleans silo), `agent` (legacy).

### macOS (Native Binary)

```bash
./deploy/deploy-all.sh macos
```

Agent binary deployed via SCP, codesigned, started with nohup.

### Both

```bash
./deploy/deploy-all.sh all
```

## Migration Status

| Component | Legacy | Orleans | Status |
|-----------|--------|---------|--------|
| Host state | AgentHub dict | HostGrain | **Dual-write** |
| Runner lifecycle | ~~RunnerTimeoutService~~ | RunnerInstanceGrain | **Grain active** |
| ~~Reconciliation~~ | ~~ReconciliationService~~ | HostGrain heartbeat | **Grain active** |
| Static provisioning | OrchestrationEngine | ProvisioningRuleGrain | Legacy active |
| Dynamic provisioning | DynamicProvisioningService | ProvisioningRuleGrain | Legacy active |
| Host selection | Inline in services | SchedulerGrain | Available |
| Webhook processing | WebhookEndpoints | WebhookProcessorGrain | Legacy active |
| UI real-time | Static events | Orleans Streams | **Streams active** |
| Backend execution | Agent (SignalR) | HostSilo (local) | Agent active, HostSilo in cluster |

**Next**: Wire HostSilo for local backend execution via grain placement, then remove Agent and SignalR hub.
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

## Profile Init Steps

Each `RunnerProfile` may carry an ordered `InitSteps` list of user-defined script
fragments that run as part of runner provisioning — **in addition to** whatever the
base image/host already provides. This lets operators layer small customizations
(e.g. `install sentry-cli`, `gh`, custom CA certs) without rebuilding a container
image or re-baking a Tart VM.

### Model

- `RunnerInitStep` (Core) — `Name`, `Phase` (PreRunner/PostExit), `Shell`
  (Auto/Bash/Sh/PowerShell/Cmd), `Script` body, `TimeoutSeconds`,
  `ContinueOnError`, optional `WorkingDirectory`, plus its own env composition
  (`EnvironmentVariableSetIds` + `EnvironmentOverrides` + `EnvironmentOverrideSecretKeys`).
- `ResolvedInitStep` (Core) — transport DTO sent to the agent: env already composed,
  `Auto` shell already collapsed.

### Server resolution (`InitStepResolver`)

For each enabled step the server builds its env as:

1. Base runner env (everything the runner itself would see)
2. Step's referenced `EnvironmentVariableSet`s (priority-ordered)
3. Step-level overrides

`Auto` shell resolves to Bash on Linux/Tart, PowerShell on Windows.

### Agent execution

- **Docker**: `InitStepShellBuilder` emits inline shell fragments that are inlined
  into the JIT entrypoint wrapper. Pre steps run before the auto-discovered
  `run.sh`/`run.cmd`; post steps run after, and the runner's exit code is preserved.
- **Tart**: Pre steps run via SSH before `config.sh`/`run.sh`/`act_runner`. Post
  steps are written as a base64-encoded wrapper script to `/tmp/rr-runner-wrapper.sh`
  on the VM, which `nohup` executes; this is required because the SSH session
  disconnects before the runner exits.
- **Native**: `InitStepExecutor` runs each step as a local child process. Pre steps
  run in `StartRunnerAsync` before the provider's Configure/Start; post steps run
  in `StopRunnerAsync` before the instance dir is cleaned up.

All steps honor per-step `TimeoutSeconds` and `ContinueOnError`. Step output is
prefixed `[init:<name>]` in the runner log.

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
