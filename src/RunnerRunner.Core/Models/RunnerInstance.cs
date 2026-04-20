namespace RunnerRunner.Core.Models;

public class RunnerInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string HostId { get; set; } = "";
    public string ProfileId { get; set; } = "";

    public required string RunnerName { get; set; }
    public RunnerInstanceStatus Status { get; set; } = RunnerInstanceStatus.Pending;
    public string? ContainerId { get; set; }
    public string? VmName { get; set; }
    public int? ProcessId { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>"static" (default, from assignments) or "dynamic" (from webhook JIT)</summary>
    public string ProvisioningMode { get; set; } = "static";

    /// <summary>WebhookEvent ID that triggered this instance (dynamic only)</summary>
    public string? WebhookEventId { get; set; }

    /// <summary>Base64-encoded JIT runner config from provider API (dynamic only)</summary>
    public string? JitConfig { get; set; }

    /// <summary>Provider job ID this instance was provisioned for (dynamic only)</summary>
    public string? JobId { get; set; }

    /// <summary>
    /// When the webhook supplied an image tag override (via
    /// <c>rr-image-tag=</c>) and the profile opted in, this holds the tag
    /// that was applied to the deploy command. Null for static runners or
    /// when the default profile tag was used.
    /// </summary>
    public string? ImageTagOverride { get; set; }

    /// <summary>
    /// True when this instance lifecycle was created and is actively managed by RunnerRunner.
    /// Null/false entries are treated as external or legacy records in the UI/capacity views.
    /// </summary>
    public bool? ManagedByRunnerRunner { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeployedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? LastHealthCheck { get; set; }

    /// <summary>Human-readable status detail, e.g. "Downloading runner v2.333.1", "Connected to GitHub"</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Provisioning rule ID that created this instance (if tracked).</summary>
    public string? ProvisioningRuleId { get; set; }

    /// <summary>Chronological log of status transitions for this instance.</summary>
    public List<StatusHistoryEntry> StatusHistory { get; set; } = [];
}
