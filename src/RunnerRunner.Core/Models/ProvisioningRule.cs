using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class ProvisioningRule
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public string? Description { get; set; }
    [Id(3)]
    public string ProfileId { get; set; } = "";
    [Id(4)]
    public ProvisioningType Type { get; set; } = ProvisioningType.Static;
    [Id(5)]
    public bool Enabled { get; set; } = true;

    // Static: fixed count
    [Id(6)]
    public int DesiredCount { get; set; } = 1;
    [Id(7)]
    public string? TargetHostId { get; set; }

    // ScaleSet: auto-scale
    [Id(8)]
    public int MinReady { get; set; } = 0;
    [Id(9)]
    public int MaxInstances { get; set; } = 5;
    [Id(10)]
    public int ScaleDownDelaySeconds { get; set; } = 300;

    // Webhook
    [Id(11)]
    public string? WebhookSecret { get; set; }
    [Id(12)]
    public List<string> AllowedOrgs { get; set; } = [];
    [Id(13)]
    public List<string> AllowedRepos { get; set; } = [];
    [Id(14)]
    public List<LabelProfileMapping> LabelMappings { get; set; } = [];
    /// <summary>Legacy fallback retained for compatibility; webhook UI now prefers explicit mappings.</summary>
    [Id(15)]
    public string? DefaultProfileId { get; set; }
    [Id(16)]
    public int MaxConcurrent { get; set; } = 10;
    [Id(17)]
    public int CooldownSeconds { get; set; } = 5;
    [Id(18)]
    public RunnerProvider Provider { get; set; }
    [Id(19)]
    public string? ProviderCredentialId { get; set; }

    // Host matching
    [Id(20)]
    public Dictionary<string, string> RequiredHostLabels { get; set; } = new();
    [Id(21)]
    public string? TargetGroupId { get; set; }

    // Scheduled (future)
    [Id(22)]
    public string? CronExpression { get; set; }

    [Id(23)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(24)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? ResolveWebhookProfileId(IEnumerable<string> labels, string? preferredProfileId = null)
    {
        var mappings = LabelMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.ProfileId) && m.RequiredLabels.Any(x => !string.IsNullOrWhiteSpace(x)))
            .OrderByDescending(m => m.Priority)
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredProfileId))
        {
            var preferred = mappings.FirstOrDefault(m =>
                string.Equals(m.ProfileId, preferredProfileId, StringComparison.OrdinalIgnoreCase)
                && m.Matches(labels));

            if (preferred != null)
                return preferred.ProfileId;
        }

        return mappings.FirstOrDefault(m => m.Matches(labels))?.ProfileId;
    }
}

public enum ProvisioningType
{
    Static,
    ScaleSet,
    Webhook,
    Scheduled
}
