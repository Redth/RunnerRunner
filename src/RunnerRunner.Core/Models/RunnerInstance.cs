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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? LastHealthCheck { get; set; }
}
