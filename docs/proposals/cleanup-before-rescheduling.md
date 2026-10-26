# Proposal: retain ownership until cleanup is confirmed

## Problem

A failed health observation is not proof that a runner has exited. Today `RunnerLifecycleManager.CollectRunnerHealthAsync` removes the tracked runner before checking whether its status establishes an exit. The HostWorker then emits `RunnerStopped` for every non-running health result. For example, a transient `tart ip` failure can turn a live VM into a stopped runner in server state and make its job eligible for another runner.

Cleanup has a similar failure: the lifecycle manager removes its tracking entry before the backend stop completes. Tart logs stop/delete errors and returns success. A failed teardown can therefore release scheduler capacity while the environment still exists, and the next retry no longer has the original tracking entry.

## Implemented change

* Keep the tracked runner until backend cleanup succeeds. Serialize cleanup per instance so the exit monitor, health sweep, and explicit stop cannot tear down the same runner concurrently. Retry with the same handle after an error; propagate cancellation without dropping ownership.
* Emit terminal health results only when cleanup succeeds. Unknown health and failed stops emit `Stopping` with an additive `CleanupPending` flag. A later healthy observation clears the flag without releasing capacity.
* Persist that flag in the runner grain and its document projection. While set, terminal grain transitions, timeout cleanup, reconciliation, provider-registration cleanup, and dynamic completion cleanup do not erase the instance or release its capacity. A stop result from the owning authenticated host clears it before the terminal transition.
* Include locally tracked instance IDs in reconciliation reports. An inventory omission does not override the worker's continuing ownership. Legacy reports omit this additive field and retain their existing reconciliation behavior.
* Require successful Tart inventory, stop, delete, and subsequent absence before returning cleanup success. Native process teardown retains its tracking entry, process object, and captured descendant identities until process/directory cleanup completes.

This is a bounded change to recovery and the resulting rescheduling decisions. It introduces no new database tables, resource-budget configuration, deployment format, or host-specific infrastructure.

## Tradeoff and limits

Uncertain cleanup can hold capacity longer. This is intentional: there is no safe timeout that proves an environment has disappeared. Existing health sweeps retry cleanup for definite exits; healthy runners remain tracked. Permanently ambiguous cases need an operator to establish the native resource's state.

This PR does not implement durable worker deployment intents, replayable cleanup receipts, atomic CPU/memory/disk admission, or adoption after a worker restart. The worker's tracking map remains in memory. It also does not fix lost acknowledgements during initial creation or replace native process-tree cleanup with process groups. Those require separate proposals; the native process-group issue is tracked in #95. This proposal addresses one source of premature replacement related to #99, without claiming to resolve every duplicate-provisioning path.

## Compatibility and rollout

Upgrade the server silos together first, then update workers one drained host at a time. Existing workers continue using legacy messages; the new guarantees require both sides to be updated. Field IDs are appended and existing serialized fields retain their IDs. No conversion of stored records is needed.

Before rolling back to a server that does not understand `CleanupPending`, drain intake and resolve retained instances. Rolling back while a runner is uncertain would restore the original unsafe timeout behavior. A worker restart also requires reconciliation/operator review; this PR does not claim automatic restart recovery.

## Verification

The tests exercise transient health errors, cancellation, failed cleanup followed by retry, concurrent stops, failed or ambiguous Tart inventory/deletion, HostWorker event classification, authenticated ownership checks, grain capacity retention and release, and timeout/reconciliation decisions. The full solution test command is:

```sh
dotnet test RunnerRunner.slnx --configuration Release --verbosity minimal
```

These are local unit and Orleans test-cluster checks. No production workers or native fleet VMs are changed or exercised by this proposal.
