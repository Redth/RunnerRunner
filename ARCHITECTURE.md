# RunnerRunner Architecture

RunnerRunner is a self-hosted CI/CD runner orchestration platform for GitHub Actions, Gitea Actions, and Azure DevOps runners. It separates the desired state of runners from the machines that execute them, then continuously reconciles rules, profiles, hosts, and runner instances until the fleet matches demand.

## System overview

```
                             +-------------------+
                             |   PostgreSQL 17   |
                             |-------------------|
                             | Orleans cluster   |
                             | Orleans state     |
                             | Orleans reminders |
                             | DocumentDB views  |
                             +---------+---------+
                                       |
                    +------------------+------------------+
                    |                                     |
        +-----------v------------+          +-------------v----------+
        | RunnerRunner.Server    |          | RunnerRunner.HostSilo  |
        |------------------------|          |------------------------|
        | Blazor UI              |          | Orleans cluster member |
        | Webhook API            |          | Host-local execution   |
        | SignalR AgentHub       |          | Docker/Tart/Native     |
        | Orleans grains         |          +------------------------+
        | Orleans Dashboard      |
        +-----------+------------+
                    |
                    | SignalR WebSocket (legacy execution path)
                    |
        +-----------v------------+
        | RunnerRunner.Agent     |
        |------------------------|
        | Connects outbound      |
        | Receives deploy/stop   |
        | Reports health/events  |
        | Docker/Tart/Native     |
        +------------------------+
```

The current system is intentionally hybrid:

- **Orleans grains** own durable control-plane state for hosts, profiles, provisioning rules, and runner lifecycle.
- **Shiny DocumentDB** stores queryable PostgreSQL read projections used by the Blazor UI and legacy services.
- **SignalR AgentHub** remains the active command channel for the legacy agent.
- **HostSilo** is the Orleans-native host execution path being introduced so runner work can execute on host-local silos without SignalR.

## Projects

| Project | Responsibility |
|---------|----------------|
| `RunnerRunner.Core` | Shared domain models, hub contracts, provider/backend interfaces |
| `RunnerRunner.Server` | Blazor UI, webhook endpoints, Orleans silo, SignalR hub, orchestration services |
| `RunnerRunner.HostSilo` | Headless Orleans silo deployed on hosts for the host-local execution path |
| `RunnerRunner.Agent` | Legacy worker deployed on hosts; executes Docker, Tart, and Native backends |
| `RunnerRunner.AppHost` | .NET Aspire local development orchestrator |
| `RunnerRunner.ServiceDefaults` | OpenTelemetry, health checks, service discovery defaults |

## Core domain model

RunnerRunner's orchestration model is easiest to understand as four layers:

| Layer | Model | Purpose |
|-------|-------|---------|
| What to run | `RunnerProfile` | Provider, labels, backend, images, environment variables, runner group, init steps, metadata behavior |
| How to run it | `ProvisioningRule` or `RunnerAssignment` | Desired count, scaling bounds, webhook mappings, host filters, concurrency ceilings |
| Where it can run | `Host` and host groups | Platform, labels, backend slot limits, environment overrides, connection status |
| What is running | `RunnerInstance` | Concrete runner lifecycle, host/profile link, provider job link, status history, resource handles |

### Runner profiles

A `RunnerProfile` defines the reusable runner template:

- CI provider: GitHub Actions, Gitea Actions, or Azure DevOps.
- Required host platform and execution backend: Docker, Tart, or Native.
- Provider credential and optional runner agent version.
- Runner labels and runner group.
- Docker/Tart image config.
- Environment variable sets plus profile-level overrides.
- `MaxParallelPerHost`, the maximum number of runners for this profile allowed on one host.
- Optional init steps executed during provisioning.
- Optional metadata labels and job-started banner hook.
- Optional webhook image tag override support.

Profiles answer "what should a runner look like?"

### Provisioning rules

`ProvisioningRule` is the rule-based desired-state model:

| Type | Key settings | Behavior |
|------|--------------|----------|
| Static | `DesiredCount`, optional target host/group/labels | Maintain a fixed number of instances for one profile |
| ScaleSet | `MinReady`, `MaxInstances`, scale-down delay | Keep a warm pool and cap total instances |
| Webhook | provider, allowed orgs/repos, label mappings, `MinReady`, `MaxConcurrent` | Match queued provider jobs to profiles and provision JIT runners |
| Scheduled | cron expression | Reserved for future scheduled capacity |

Webhook rules can map different `runs-on` label sets to different profiles. Mappings are ordered by priority; a preferred profile can win only if its mapping still matches the job labels.

`RunnerAssignment` is the legacy static model: one host, one profile, one desired count. It is still reconciled by `OrchestrationEngine`.

### Hosts

A `Host` represents a physical or virtual machine. It carries:

- Platform (`Linux`, `MacOS`, `Windows`) and optional architecture.
- Capability labels such as `os=linux`, `arch=x64`, `docker=true`, `pool=build-farm`.
- Optional `GroupId`.
- Environment overrides applied after profile env vars.
- Backend slot limits:
  - `MaxDockerContainers`
  - `MaxTartVMs`
  - `MaxNativeProcesses`

A backend slot limit of `0` disables that backend on the host.

### Runner instances

A `RunnerInstance` is one concrete runner. Capacity calculations count RunnerRunner-managed instances in any active lifecycle state:

- `Pending`
- `Starting`
- `Running`
- `Stopping`

Terminal states (`Stopped`, `Failed`, `Crashed`) do not consume capacity.

## Control-plane flows

### Static assignments

Legacy static assignment flow:

1. User assigns a profile to a host with a desired count.
2. `OrchestrationEngine` wakes every 15 seconds.
3. It counts active instances for the host/profile assignment.
4. If current is below desired, it creates runner grains/instances and sends `DeployRunner` to the connected agent.
5. If current is above desired, it sends `StopRunner`.
6. Old terminal runner records are removed after a grace period.

This path is host-specific and does not perform rule-level host selection.

### Rule reconciliation

Orleans `ProvisioningRuleGrain` reconciles rule-owned capacity:

1. Enabled Static and ScaleSet rules start a grain timer.
2. The timer calls `Reconcile()` every 30 seconds.
3. Static rules compare alive instances to `DesiredCount`.
4. ScaleSet rules ensure at least `MinReady` alive instances, capped by `MaxInstances`.
5. Webhook rules can keep `MinReady` idle warm runners.
6. Dead instances are removed from the rule's managed instance list.

When a rule needs a runner, it resolves the profile, analyzes matching host capacity, initializes a `RunnerInstanceGrain`, and tracks the instance ID in the rule state.

### Webhook/JIT provisioning

Webhook provisioning is job-driven:

1. Provider sends a `workflow_job` event to `/api/webhooks/{provider}`.
2. The server validates the signature and records a `WebhookEvent`.
3. The event is matched to an enabled webhook provisioning rule by provider and allowed org/repo.
4. The job's labels are matched against the rule's label mappings to resolve a profile.
5. FIFO, rule, profile, and host capacity checks run.
6. A host is selected.
7. JIT config or registration token is generated for the provider.
8. A dynamic `RunnerInstance` is initialized.
9. `DeployRunner` is sent to the host's connected agent.
10. The queued event is marked `provisioned` and linked to the instance.
11. `in_progress` and `completed` provider events confirm execution and cleanup.

Pending webhook events are retried by `DynamicProvisioningService`. If webhook delivery is missed, GitHub queued/in-progress/requested/waiting jobs are periodically backfilled through the GitHub API.

## Capacity and concurrency rules

RunnerRunner calculates whether work can start by applying capacity in this order:

1. **FIFO fairness**
2. **Provisioning rule capacity**
3. **Profile per-host capacity**
4. **Host backend capacity**
5. **Connected execution channel**

The first exhausted layer becomes the visible blocker: `FIFO`, `ProvisioningRule`, `Profile`, `Host`, `Matching`, or `Configuration`.

### 1. FIFO fairness

Queued webhook jobs are processed oldest first within the same provisioning lane. A newer event waits if an earlier queued event:

- belongs to the same provisioning rule, or
- needs the same host platform and execution backend.

This prevents a flood of newer jobs from jumping ahead of older queued work that competes for the same runners.

### 2. Rule-level capacity

Each rule type has a rule-level ceiling:

| Rule type | Ceiling used for capacity |
|-----------|---------------------------|
| Static | `DesiredCount` |
| ScaleSet | `MaxInstances` |
| Webhook | `MaxConcurrent` |
| Scheduled | currently treated like desired count until implemented |

For webhook rules, active dynamic instances linked to events for the rule count against `MaxConcurrent`. Active instances include `Pending`, `Starting`, `Running`, and `Stopping`.

For ScaleSet rules, `MinReady` is the warm-pool floor and `MaxInstances` is the ceiling. The warm pool cannot exceed the max.

### 3. Profile per-host capacity

`RunnerProfile.MaxParallelPerHost` limits how many active instances of that profile may exist on a single host.

This is intentionally separate from host backend capacity. A large host might allow 10 Docker containers, but a profile can still cap itself at 1 runner per host to avoid noisy-neighbor contention or provider label ambiguity.

### 4. Host backend capacity

Each host has a backend-specific slot limit:

| Backend | Host limit |
|---------|------------|
| Docker | `MaxDockerContainers` |
| Tart | `MaxTartVMs` |
| Native | `MaxNativeProcesses` |

Only instances whose profile uses that backend consume the backend slots. A Docker runner does not consume Tart or Native capacity.

### 5. Host matching and execution availability

Before capacity is summed, hosts must match:

- host platform equals `RunnerProfile.RequiredHostPlatform`;
- rule `TargetHostId`, if set;
- rule `TargetGroupId`, if set;
- every rule `RequiredHostLabels` key/value pair;
- Docker host compatibility, when a host advertises `docker_os`.

The legacy SignalR execution path also requires a connected agent for the selected host. If capacity exists but no agent is connected, the webhook event is retried as a host-match/execution-channel wait rather than consuming a runner slot forever.

### Effective pool math

For a single profile under a rule:

```
per-host usable slots = min(
  profile.MaxParallelPerHost - active profile instances on host,
  host backend limit - active backend instances on host
)

available pool slots = sum(per-host usable slots across matching hosts)

rule remaining slots = rule ceiling - active instances counted against rule

can start now = FIFO is clear
             and rule remaining slots > 0
             and available pool slots > 0
             and selected host has an execution channel
```

The configured pool limit shown in UI is:

```
sum(min(profile.MaxParallelPerHost, host backend limit) across matching hosts)
```

The available-now value subtracts active profile and backend usage first.

Example:

| Setting | Value |
|---------|-------|
| Webhook rule `MaxConcurrent` | 10 |
| Matching hosts | 3 Linux Docker hosts |
| Profile `MaxParallelPerHost` | 1 |
| Each host `MaxDockerContainers` | 4 |

The rule appears to allow 10 concurrent jobs, but the effective pool limit is `3 * min(1, 4) = 3`. If all three hosts already run that profile, new matching webhook jobs are blocked by `Profile`, not by the rule or host backend.

If `MaxParallelPerHost` is raised to 4, the effective pool limit becomes `3 * min(4, 4) = 12`, and the rule's `MaxConcurrent = 10` becomes the narrower limit.

### Host selection

When at least one host can run the profile, host candidates are ordered by:

1. can run now before blocked candidates;
2. lowest total active host load;
3. host label/name for deterministic tie-breaking.

If every matching host is saturated by `MaxParallelPerHost`, the blocker is `Profile`. If at least one host still has profile room but all are out of backend slots, the blocker is `Host`.

## Environment variable composition

Runner environment variables are layered with later layers overriding earlier ones:

1. Provider credential variables injected as `RR_*`.
2. Profile-selected `EnvironmentVariableSet` documents ordered by ascending priority.
3. Profile-level environment overrides.
4. Host-level environment overrides.
5. Instance variables such as `RR_INSTANCE_ID` and `RR_RUNNER_NAME`.

After composition, `$VAR` and `${VAR}` references are expanded for up to three passes so values can chain through other variables.

Init steps receive the runner environment plus their own environment variable sets and overrides. `Auto` shell resolves by platform/backend before commands are sent to the agent.

## Execution backends

| Backend | Typical platform | Execution model |
|---------|------------------|-----------------|
| Docker | Linux, Windows | Starts one container per runner; image supplies the runner environment; JIT config can be injected into the entrypoint |
| Tart | macOS | Clones/starts a VM image, copies config/scripts, then runs the provider runner inside the guest |
| Native | Any | Downloads/configures the runner and starts it as a local process with PID/log tracking |

All backends receive the same `DeployRunnerCommand` shape: instance ID, profile ID, runner name, backend, provider, labels, environment variables, image config, runner URL/token/JIT config, work paths, registry credentials, and resolved init steps.

## Runner lifecycle and health

Runner instances follow this lifecycle:

```
Pending -> Starting -> Running -> Stopping -> Stopped
   |          |           |           |
   |          |           |           +-> Failed
   |          |           +-> Crashed
   |          +-> Failed
   +-> Failed
```

Important timeout and recovery behavior:

- Deployment and registration failures move an instance to `Failed`.
- Missing health/reconciliation from the host can move a running instance to `Crashed`.
- Stopping can fail if the host does not complete cleanup.
- Dynamic runners linked to queued jobs can cause the webhook event to retry when the runner disappears before the job starts.
- Reconciliation reports clean up orphaned Docker containers, Tart VMs, or native processes that are present on the host but absent from the database.

## State, projections, and UI refresh

RunnerRunner maintains two durable views:

1. **Orleans grain state** in PostgreSQL: lifecycle ownership, timers, durable grain state, reminders, cluster membership.
2. **DocumentDB projections** in PostgreSQL: queryable documents used by Blazor pages and legacy services.

Grains sync important state changes to DocumentDB so the UI can query efficiently. Orleans streams publish runner and host status changes, and `StreamSubscriptionService` bridges those events to Blazor components for real-time refresh.

## Web UI surfaces

Key UI pages map directly to the domain model:

| Page | Purpose |
|------|---------|
| Dashboard | Fleet overview and connected hosts |
| Hosts | Host labels, backend limits, approval, assignments |
| Runners | Runner instances grouped by host/profile/status |
| Jobs | Job-centric lifecycle and webhook context |
| Profiles | Runner templates, labels, env vars, init steps, images |
| Provisioning Rules | Static, ScaleSet, Webhook, and future Scheduled rules |
| Images | Docker/Tart image management |
| Logs | Agent and runner log viewing |
| Env Variables | Reusable environment variable sets |
| Webhooks / Webhook Events | Provider webhook bindings and audit trail |
| Settings | Provider and registry credentials |
| Orleans Dashboard | Grain/silo observability |

Capacity views on rules, profiles, hosts, runners, and events use the same `CapacityPlanningService` calculations described above, so blockers in the UI reflect actual scheduling decisions.

## Migration status

| Area | Legacy path | Orleans path | Current state |
|------|-------------|--------------|---------------|
| Host connection | SignalR AgentHub | HostGrain/HostSilo | Hybrid |
| Runner lifecycle | Agent callbacks and services | RunnerInstanceGrain | Grain state active, agent execution active |
| Static provisioning | `RunnerAssignment` + `OrchestrationEngine` | `ProvisioningRuleGrain` Static | Both models present |
| Webhook provisioning | `DynamicProvisioningService` | `ProvisioningRuleGrain` Webhook | Legacy service active with grain-backed instances |
| Host selection | Capacity service + connected agents | Scheduler/host grains | Capacity service active |
| Real-time UI | Static events | Orleans streams | Streams active |
| Backend execution | SignalR `DeployRunner` to agent | Host-local silo execution | Agent active, HostSilo introduced |

The migration direction is to keep the same domain model and capacity rules while moving host execution from SignalR agents to Orleans host-local silos.
