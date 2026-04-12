namespace RunnerRunner.Core.Models;

/// <summary>
/// Maps a webhook source (org/repo) to runner profiles for dynamic JIT provisioning.
/// </summary>
public class WebhookBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public RunnerProvider Provider { get; set; }
    public string? ProviderCredentialId { get; set; }

    /// <summary>
    /// Which orgs are allowed to trigger. Empty = use credential's scope.
    /// </summary>
    public List<string> AllowedOrgs { get; set; } = [];

    /// <summary>
    /// Which repos are allowed (full name like "org/repo"). Empty = all in allowed orgs.
    /// </summary>
    public List<string> AllowedRepos { get; set; } = [];

    /// <summary>
    /// Priority-ordered label→profile mappings. First match wins.
    /// </summary>
    public List<LabelProfileMapping> Mappings { get; set; } = [];

    /// <summary>
    /// Fallback profile if no label mapping matches.
    /// </summary>
    public string? DefaultProfileId { get; set; }

    /// <summary>
    /// HMAC-SHA256 secret for webhook signature validation.
    /// </summary>
    public string WebhookSecret { get; set; } = "";

    public int MaxConcurrentJobs { get; set; } = 10;
    public int CooldownSeconds { get; set; } = 5;

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Maps a set of required labels to a runner profile.
/// If all RequiredLabels are present in the job's runs-on labels, this mapping matches.
/// </summary>
public class LabelProfileMapping
{
    public List<string> RequiredLabels { get; set; } = [];
    public string ProfileId { get; set; } = "";

    /// <summary>Higher priority = checked first.</summary>
    public int Priority { get; set; } = 0;
}
