# Durable admission, cleanup and worker recovery

A timeout, lost acknowledgement or failed health observation does not establish that a container has disappeared. Releasing its scheduling slot can start a second environment while the first still owns CPU, memory, disk and a runner registration.

## Opt in without changing existing hosts

Hosts without a job budget keep existing scheduling, including Native backends. Container CPU, memory and writable-layer limits remain optional on those hosts. Windows isolation stays at the Docker daemon default unless the profile selects Hyper-V.

To enable durable admission, upgrade the server, drain the host, complete existing jobs, and upgrade its worker. Confirm that no old native environments remain. In Hosts, enable the job budget and enter the CPU, memory, disk and Docker/Tart slot capacity available after reserving the host's own resources. Disable Native capacity on this host: Native admission is not implemented. A zero budget closes admission. Saving labels on an unbudgeted host does not enable the feature.

Every budgeted Docker/Tart profile needs positive resource amounts. Linux Docker writable-layer quotas require a compatible storage driver; an unsupported daemon rejects creation. The final admission transaction serializes all profiles sharing a host, so stale dashboard counts cannot overcommit it. A worker must advertise `durable-ownership-v1` before the server reserves or dispatches budgeted work. Existing workers cannot silently ignore the new contract.

## Ownership and cleanup

PostgreSQL stores each reservation before dispatch. A send failure retains it because delivery may have happened. A unique instance ID cannot be reused, even after cleanup or by another host. Budget reductions cannot undercut outstanding reservations.

The worker flushes a single-use deployment intent before native creation and binds the image digest, resource settings, instance, profile, host and nonce before calling Docker. It records the immutable container ID immediately after creation. No registration tokens, registry passwords or job environment values are persisted in that journal. One process can own a worker data directory at a time.

Cleanup retains the live tracking entry until the backend confirms completion. Unknown health does not emit a terminal receipt. A cleanup receipt must match the authenticated host, backend and recorded handle, or establish that creation never began. The server commits release before acknowledging the receipt. The worker replays unacknowledged receipts across reconnections and restarts; the server accepts valid duplicates after runner documents have been purged.

Timeouts, reconciliation and registration cleanup preserve unreleased reservations. Inventory omissions and name prefixes are not grounds to delete environments. Superseded gRPC connections cannot submit further events.

## Restart recovery and its limits

Docker recovery inspects the exact owned container. A running match is observed without replaying startup or JIT credentials. A created or exited match is cleaned up. Absence releases ownership only when it refers to a recorded immutable container ID; a missing name after an uncertain Create response remains unresolved.

Tart and Native do not implement immutable restart adoption. An untracked name or PID is insufficient proof for automatic deletion. These uncertain cases retain ownership for operator reconciliation. Native process groups and automatic repair of corrupt/lost worker journals are outside this change.

Uncertainty can therefore hold capacity indefinitely. Back up the control database and each worker's durable data directory, and do not delete reservations or journals to force scheduling. Before downgrading, drain and resolve outstanding reservations; older servers do not enforce them.

## Readiness before GitHub registration

`DockerImageConfig.PrepareBeforeRegistration` is an opt-in Windows/GitHub feature for budgeted hosts. Supply mandatory bounded PreRunner checks and an image containing `C:\actions-runner\run.cmd` or `C:\runner\run.cmd`. The worker boots the owned environment without registration credentials and runs the checks. Only after that authenticated prepared receipt does the server claim and request JIT registration once. Activation arrives over the private Docker transport; it is not stored in the host journal or logged. Prepared JIT containers never use Docker restart policies to replay their job.

A lost provider or activation reply remains uncertain. It does not cause another JIT request or another environment. Recovery cleans up a prepared environment that was never activated, and observes an activation whose outcome is uncertain without replaying it.

An optional `HostWorker:JobDocker` gateway lets Windows jobs use an already provisioned Linux Docker VM. It is disabled by default. Configuration binds the exact VM ID, installation/instance notes, disk path, MAC, private NAT subnet and CA public hash. The Linux daemon must already present a certificate valid for the stable gateway address. Configure the narrowly scoped firewall rule using `JobDockerFirewall.ps1` during installation; startup can rebind that owned rule to a replacement HNS adapter but does not create arbitrary network access. The worker verifies mTLS/API readiness before connecting to the scheduler, issues per-job client leaves, and verifies the expected daemon inside the prepared container. Host CA/private infrastructure keys stay on the host. The feature does not create VMs, install Docker, assign MDM policies or publish images.

Private control-plane CA trust and isolated listeners are described in #111. Digest image references, registry selection and persistence fixes are also submitted separately in #106–#108; this feature includes the compatible integration of those changes.

## Verification

Run `dotnet test RunnerRunner.slnx --configuration Release` and the repository coverage script. Tests cover uncertain health/cleanup, cancellation, durable creation boundaries, duplicate delivery, restart adoption, malformed/foreign ownership, cleanup receipt replay, prepared registration and legacy unbudgeted dispatch.

`tests/RunnerRunner.AdmissionProbe` provides an explicit PostgreSQL integration check in a new schema. It accepts `phase1|phase2 CONFIG UNIQUE_SCHEMA RESULTS_DIRECTORY`; CONFIG contains `Database.ConnectionString`, and the schema must start with `rr_admission_probe_`. Run against an isolated test database. Phase 1 races 96 independent requests across shared and individual resource limits. Phase 2 is a separate process that checks retained reservations, identity-bound cleanup, rejected late/duplicate starts and fresh replacement admission. It preserves results and does not execute runner jobs.

The upstream patch was tested locally with the unit suite, coverage gates and both PostgreSQL phases. Its generalized configuration was not redeployed to production Windows/macOS machines as part of this PR.
