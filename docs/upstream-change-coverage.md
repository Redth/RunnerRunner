# Downstream change review

This series separates general RunnerRunner fixes from deployment choices. The review covers the committed downstream changes against upstream v0.5.11; ongoing deployment experiments are excluded.

| Area | Upstream coverage |
| --- | --- |
| Disabled rules and zero-valued limits surviving Orleans persistence | #106 |
| Digest/tag identity through Docker pull, inspect and create | #107 |
| Backend-specific registry selection and private Tart clone credentials | #108 |
| Atomic CPU/memory/disk/slot budgets, durable ownership, cleanup receipts, Docker restart recovery | #109 |
| Optional Windows Hyper-V/resource controls, image override propagation and profile editing | #109 |
| Readiness before JIT registration and optional mTLS Linux Docker access from Windows jobs | #109 |
| Conservative Tart/native cleanup and Docker exit-status reporting | #109 |
| Windows init exit propagation and Tart credential/log handling | #110, integrated where needed by #109 |
| Private control CA trust, worker-only listener, Orleans bind configuration | #111, integrated by #109 |
| Windows installer retry without force-stopping a matching worker; token-file input | #112 |

#109 includes the integration of its smaller prerequisites so it can build independently. The smaller PRs remain available for separate review and merge. Each PR targets upstream main and is ready for review.

## Deliberately excluded

* Company registry namespaces, package IDs, release URLs, Compose settings and publication workflows. Upstream must choose its own publishing and access policy; a downstream private-registry default is not a general fix.
* The MDM-specific OS/host package graph, Windows feature-reboot coordinator, disposable Linux template/VM bootstrap and macOS system LaunchDaemon package. These are coupled deployment changes. Fresh-install interruption, registry delivery and secure no-login macOS acceptance are still incomplete; source-level tests do not establish those guarantees. The independently useful installer retry fix is extracted in #112.
* Deletion of upstream's interactive macOS and SSH deployment entry points, and replacement of its README with an organization's rollout instructions. Those are downstream product choices, not upstream defects.
* Hardcoded native-machine qualification programs, physical serials, infrastructure paths, recovery artifacts and test credentials. Generic unit tests and the isolated PostgreSQL concurrency/restart harness are included instead.
* A blanket change to strict Tart SSH verification without a complete trusted-host-key delivery interface. That private change would break fresh VM connections for upstream users. #110 removes password/script exposure while preserving existing connection behavior; host-key enrollment needs a complete, separately tested interface before changing its default.
* Native process-group supervision and immutable Tart restart adoption. The downstream code does not implement those features. #109 retains uncertain ownership instead of claiming that a name or recycled PID proves identity.

These exclusions are limits of the available implementation, not claims that the deployment features are unnecessary. No production provisioning, live worker, MDM assignment or image publication is changed by this upstream series.
