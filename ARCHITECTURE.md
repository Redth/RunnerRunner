# RunnerRunner Architecture

RunnerRunner is a self-hosted CI/CD runner orchestration platform for GitHub Actions, Gitea Actions, and Azure DevOps runners. It separates the desired state of runners from the machines that execute them, then continuously reconciles rules, runner targets, hosts, and runner instances until the fleet matches demand.

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
        | RunnerRunner.Server    |          | RunnerRunner.HostWorker  |
        |------------------------|          |------------------------|
        | Blazor UI              |          | Authenticated gRPC     |
        | Webhook API            |          | Host-local execution   |
        | Orleans grains/streams |          | Docker/Tart/Native     |
        | Orleans grains         |          +------------------------+
        | Orleans Dashboard      |
        +-----------+------------+
```

The current system uses HostWorker as the only supported host-worker runtime:

- **Orleans grains** own durable control-plane state for hosts, provisioning rules, rule-owned runner targets, and runner lifecycle.
- **Shiny DocumentDB** stores queryable PostgreSQL read projections used by the Blazor UI and orchestration services.
- **gRPC HostWorker streams** carry host commands, worker heartbeats, runner events, image events, and log frames between the server and workers.
- **HostWorker** executes runner work locally without joining the Orleans cluster or needing database credentials.
- **Host enrollment tokens** are generated per host from the server UI and stored hashed; a shared bootstrap token remains only for bundled/self-contained compose installs.

## Projects

| Project | Responsibility |
|---------|----------------|
| `RunnerRunner.Core` | Shared domain models, command/event contracts, provider/backend interfaces |
| `RunnerRunner.Server` | Blazor UI, webhook endpoints, Orleans silo, orchestration services |
| `RunnerRunner.HostWorker` | Authenticated outbound worker deployed on hosts for local runner execution |
| `RunnerRunner.Agent` | Reusable runner backend/lifecycle library for Docker, Tart, and Native execution |
| `RunnerRunner.AppHost` | .NET Aspire local development orchestrator |
| `RunnerRunner.ServiceDefaults` | OpenTelemetry, health checks, service discovery defaults |

## Core domain model

RunnerRunner's orchestration model is easiest to understand as four layers:

| Layer | Model | Purpose |
|-------|-------|---------|
| What to run | Rule-owned `RunnerDefinition` | Workflow target key, advertised labels/capabilities, backend, images, environment variables, provider group/pool, target-specific host routing, custom-step references, metadata behavior |
| How to run it | `ProvisioningRule` or `RunnerAssignment` | Provider, desired count, scaling bounds, webhook scope, optional global host filters, concurrency ceilings |
| Where it can run | `Host` and host routing groups | Platform, labels, backend slot limits, environment overrides, connection status |
| What is running | `RunnerInstance` | Concrete runner lifecycle, host/runner-definition link, provider job link, status history, resource handles |

### Rule-owned runner targets

A `ProvisioningRule` owns the runner target(s) it can provision:

- Stable workflow target key, for example `rr-linux-docker`.
- Required host platform and execution backend: Docker, Tart, or Native.
- Optional runner agent version.
- Advertised runner labels/capabilities and provider runner group/pool.
- Optional target-specific host routing, such as a required host group or HostWorker capabilities like `windows-ui`.
- Docker/Tart image config.
- Environment variable sets plus runner-level overrides.
- Live references to reusable custom steps plus optional inline steps.
- Optional metadata labels and job-started banner hook.
- Optional webhook image tag override support.
- Runner lifecycle: **one-job runner** by default, or explicit reusable runner mode for multi-job static/warm pools.

Runner targets answer "what should this rule provision, and what key should workflow authors request?" The CI provider and credential are rule-level settings so webhook authentication, JIT config, cleanup, and static/scale-set registration use the same source of truth.

### Provisioning rules

`ProvisioningRule` is the rule-based desired-state model:

| Type | Key settings | Behavior |
|------|--------------|----------|
| Static | provider, runner target, `DesiredCount`, optional global target host/group/labels | Maintain a fixed number of instances for one runner target |
| ScaleSet | provider, runner target, `MinReady`, `MaxInstances`, scale-down delay | Keep a warm pool and cap total instances |
| Webhook | provider, allowed orgs/repos, runner target keys, `MinReady`, `MaxConcurrent` | Match queued provider jobs to rule-owned runner targets and provision JIT runners |
| Scheduled | cron expression | Reserved for future scheduled capacity |

Webhook rules already know the incoming DevOps provider from the webhook endpoint and signature. Workflow authors request a runner target key such as `rr-macos-arm64`, and RunnerRunner selects the enabled target in that rule with the same key. GitHub and Gitea use the target key as a self-hosted runner label, for example `runs-on: [self-hosted, rr-macos-arm64]`; Azure DevOps uses the same product concept through pool demands, for example `rr.target -equals rr-windows-native`.

Legacy label matchers remain available as an advanced compatibility path, but the primary routing model is the explicit target key. Advertised labels/capabilities are still attached to created runners for provider dispatch and diagnostics; they are not the main product-level selector.

Keep similarly named concepts at different layers:

- **Runner targets** are workflow selectors.
- **Global host routing groups/labels** narrow all work accepted by a rule; **target host routing** adds per-runner requirements such as `windows-ui`.
- **Provider runner groups / Azure pools** decide where the registered runner is visible inside the CI provider.

`RunnerAssignment` is the legacy static model: one host, one profile, one desired count. It is still reconciled by `OrchestrationEngine`.

### Hosts

A `Host` represents a physical or virtual machine. It carries:

- Platform (`Linux`, `MacOS`, `Windows`) and optional architecture.
- Advertised capabilities such as `docker`, `native`, `tart`, `gpu`, or `windows-ui`.
- Key/value host labels such as `os=linux`, `arch=x64`, `docker=true`, `pool=build-farm`.
- Optional routing `GroupId`.
- Environment overrides applied after runner target env vars.
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

Static assignment flow:

1. User assigns a profile to a host with a desired count.
2. `OrchestrationEngine` wakes every 15 seconds.
3. It counts active instances for the host/profile assignment.
4. If current is below desired, it creates runner grains/instances and sends `DeployRunner` to HostWorker over gRPC.
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

When a rule needs a runner, it resolves the runner target, analyzes matching host capacity, initializes a `RunnerInstanceGrain`, and tracks the instance ID in the rule state.

### Webhook/JIT provisioning

Webhook provisioning is job-driven:

1. Provider sends a `workflow_job` event to `/api/webhooks/{provider}`.
2. The server validates the signature and records a `WebhookEvent`.
3. The event is matched to an enabled webhook provisioning rule by provider and allowed org/repo.
4. The job's runner selection is matched to a runner target key, with legacy label matchers used only as fallback.
5. FIFO, rule, and host capacity checks run.
6. A host is selected.
7. JIT config or registration token is generated for the provider.
8. A dynamic `RunnerInstance` is initialized.
9. `DeployRunner` is sent to the selected HostWorker over gRPC.
10. The queued event is marked `provisioned` and linked to the instance.
11. `in_progress` and `completed` provider events confirm execution and cleanup.

Pending webhook events are retried by `DynamicProvisioningService`. If webhook delivery is missed, GitHub queued/in-progress/requested/waiting jobs are periodically backfilled through the GitHub API.

## Capacity and concurrency rules

RunnerRunner calculates whether work can start by applying capacity in this order:

1. **FIFO fairness**
2. **Provisioning rule capacity**
3. **Host backend capacity**
4. **Connected execution channel**

The first exhausted layer becomes the visible blocker: `FIFO`, `ProvisioningRule`, `Host`, `Matching`, or `Configuration`.

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

### 3. Host backend capacity

Each host has a backend-specific slot limit:

| Backend | Host limit |
|---------|------------|
| Docker | `MaxDockerContainers` |
| Tart | `MaxTartVMs` |
| Native | `MaxNativeProcesses` |

Only instances whose runner target uses that backend consume the backend slots. A Docker runner does not consume Tart or Native capacity.

### 4. Host matching and execution availability

Before capacity is summed, hosts must match:

- host platform equals the runner target's required host platform;
- rule `TargetHostId`, if set;
- rule host routing `TargetGroupId`, if set;
- every rule `RequiredHostLabels` key/value pair;
- Docker host compatibility, when a host advertises `docker_os`.

HostWorker execution also requires the selected host to be online in the Orleans cluster. If capacity exists but the HostWorker is offline, the webhook event is retried as a host-match/execution-channel wait rather than consuming a runner slot forever.

### Effective pool math

For a single runner target under a rule:

```
per-host usable slots = host backend limit - active backend instances on host

available pool slots = sum(per-host usable slots across matching hosts)

rule remaining slots = rule ceiling - active instances counted against rule

can start now = FIFO is clear
             and rule remaining slots > 0
             and available pool slots > 0
             and selected host has an execution channel
```

The configured pool limit shown in UI is:

```
sum(host backend limit across matching hosts)
```

The available-now value subtracts active backend usage first.

Example:

| Setting | Value |
|---------|-------|
| Webhook rule `MaxConcurrent` | 10 |
| Matching hosts | 3 Linux Docker hosts |
| Each host `MaxDockerContainers` | 4 |

The host route can run up to `3 * 4 = 12` Docker runners, but the rule's `MaxConcurrent = 10` is narrower, so only 10 matching jobs can run at once for that rule. If all Docker slots on those hosts are already consumed, new matching webhook jobs are blocked by `Host`.

### Host selection

When at least one host can run the runner target, host candidates are ordered by:

1. can run now before blocked candidates;
2. lowest total active host load;
3. host label/name for deterministic tie-breaking.

If every matching host is out of backend slots, the blocker is `Host`.

## Environment variable composition

Runner environment variables are layered with later layers overriding earlier ones:

1. Provider credential variables injected as `RR_*`.
2. Runner-target-selected `EnvironmentVariableSet` documents ordered by ascending priority.
3. Runner-target-level environment overrides.
4. Host-level environment overrides.
5. Instance variables such as `RR_INSTANCE_ID` and `RR_RUNNER_NAME`.

After composition, `$VAR` and `${VAR}` references are expanded for up to three passes so values can chain through other variables.

Custom steps receive the runner environment plus their own environment variable sets and overrides. `Auto` shell resolves by platform/backend before commands are sent to the agent.

## Execution backends

| Backend | Typical platform | Execution model |
|---------|------------------|-----------------|
| Docker | Linux, Windows | Starts one container per runner; image supplies the runner environment; JIT config can be injected into the entrypoint |
| Tart | macOS | Clones/starts a VM image, copies config/scripts, then runs the provider runner inside the guest |
| Native | Any | Downloads/configures the runner and starts it as a local process with PID/log tracking |

All backends receive the same `DeployRunnerCommand` shape: instance ID, runner target/profile compatibility ID, runner name, backend, provider, labels, environment variables, image config, runner URL/token/JIT config, work paths, registry credentials, and resolved custom steps.

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
2. **DocumentDB projections** in PostgreSQL: queryable documents used by Blazor pages and orchestration services.

Grains sync important state changes to DocumentDB so the UI can query efficiently. Orleans streams publish runner and host status changes, and `StreamSubscriptionService` bridges those events to Blazor components for real-time refresh.

## Web UI surfaces

Key UI pages map directly to the domain model:

| Page | Purpose |
|------|---------|
| Dashboard | Fleet overview and connected hosts |
| Hosts | Host labels, backend limits, approval, assignments |
| Runners | Runner instances grouped by host/runner target/status |
| Jobs | Job-centric lifecycle and webhook context |
| Provisioning Rules | Static, ScaleSet, Webhook, and future Scheduled rules with rule-owned runner targets |
| Custom Steps | Reusable provisioning scripts referenced by runner targets |
| Images | Docker/Tart image management |
| Logs | HostWorker and runner log viewing |
| Env Variables | Reusable environment variable sets |
| Webhooks / Webhook Events | Provider webhook bindings and audit trail |
| Settings | Provider and registry credentials |
| Orleans Dashboard | Grain/silo observability |

Capacity views on rules, runner targets, hosts, runners, and events use the same `CapacityPlanningService` calculations described above, so blockers in the UI reflect actual scheduling decisions.

## Runtime status

| Area | Runtime path |
|------|--------------|
| Host connection | HostGrain plus authenticated HostWorker gRPC connection |
| Runner lifecycle | RunnerInstanceGrain state with HostWorker-local execution |
| Static provisioning | `RunnerAssignment` plus `ProvisioningRuleGrain` Static |
| Webhook provisioning | `DynamicProvisioningService` with grain-backed runner instances |
| Host selection | Capacity service using online HostWorker state and host capabilities |
| Real-time UI | Orleans streams plus server-side HostWorker log cache |
| Backend execution | gRPC commands to HostWorker |
| HostWorker updates | Server checks GitHub Releases, then sends verified update commands over gRPC |

## Release and deployment model

RunnerRunner's public deployment path is release-artifact driven. GitHub Releases publish the Linux compose bundle, native HostWorker assets, checksums, and `release-manifest.json`; server installs update through `runnerrunner-server update [ref]` for release tags, branches, or commit SHAs; native HostWorkers update through the Hosts page after the server checks the update catalog.

The SSH-based scripts in `deploy/` remain useful for development and lab debugging because they can push local builds to remote machines. They are not the consumer-facing install or update mechanism and should not be treated as the primary production deployment model.

## HostWorker updates

The server treats GitHub refs as the update catalog. Release tags use GitHub Release assets first; branches and commits resolve to the matching successful workflow artifacts. The server reads `release-manifest.json` for asset checksums, selects the correct HostWorker asset by platform/runtime identifier, and exposes update availability on the Hosts page.

When an operator clicks **Update**, the server sends `ApplyHostWorkerUpdate` to the connected worker. The worker downloads the asset directly from GitHub, verifies SHA256, extracts it to a staging directory, then hands off restart/copy work to launchd, Windows Service recovery, systemd/container restart policies, or an optional configured restart command. Containerized HostWorkers pull the manifest-selected image and start a replacement container before stopping the old one.

## Logs and observability

HostWorkers write command journals and log streams locally first, then publish batched log frames over the authenticated gRPC stream. The server stores a bounded recent cache keyed by host and stream so the Logs and Jobs pages can show recent output even when a worker is offline or a live log request times out.

OpenTelemetry is enabled through `RunnerRunner.ServiceDefaults` for server and worker logs, metrics, and traces. OTLP export is opt-in via `OTEL_EXPORTER_OTLP_ENDPOINT`; exporter failures are not part of runner correctness. The Linux compose bundle includes an optional `observability` profile with an OpenTelemetry Collector, and production installs can replace that collector/exporter with Grafana/Loki/Tempo/Prometheus, Seq, Azure Monitor, or another OTLP-compatible backend.
