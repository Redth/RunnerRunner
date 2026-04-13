namespace RunnerRunner.Core.Models;

public class ProvisioningRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string ProfileId { get; set; } = "";
    public ProvisioningType Type { get; set; } = ProvisioningType.Static;
    public bool Enabled { get; set; } = true;

    // Static: fixed count
    public int DesiredCount { get; set; } = 1;
    public string? TargetHostId { get; set; }

    // ScaleSet: auto-scale
    public int MinReady { get; set; } = 0;
    public int MaxInstances { get; set; } = 5;
    public int ScaleDownDelaySeconds { get; set; } = 300;

    // Webhook
    public string? WebhookSecret { get; set; }
    public List<string> AllowedOrgs { get; set; } = [];
    public List<string> AllowedRepos { get; set; } = [];
    public List<LabelProfileMapping> LabelMappings { get; set; } = [];
    public string? DefaultProfileId { get; set; }
    public int MaxConcurrent { get; set; } = 10;
    public int CooldownSeconds { get; set; } = 5;
    public RunnerProvider Provider { get; set; }
    public string? ProviderCredentialId { get; set; }

    // Host matching
    public Dictionary<string, string> RequiredHostLabels { get; set; } = new();
    public string? TargetGroupId { get; set; }

    // Scheduled (future)
    public string? CronExpression { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ProvisioningType
{
    Static,
    ScaleSet,
    Webhook,
    Scheduled
}
