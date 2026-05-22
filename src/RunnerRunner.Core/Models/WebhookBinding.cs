using Orleans;
using System.Text.RegularExpressions;

namespace RunnerRunner.Core.Models;

/// <summary>
/// Maps a webhook source (org/repo) to runner profiles for dynamic JIT provisioning.
/// </summary>
[GenerateSerializer]
public class WebhookBinding
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public RunnerProvider Provider { get; set; }
    [Id(3)]
    public string? ProviderCredentialId { get; set; }

    /// <summary>
    /// Which orgs are allowed to trigger. Empty = use credential's scope.
    /// </summary>
    [Id(4)]
    public List<string> AllowedOrgs { get; set; } = [];

    /// <summary>
    /// Which repos are allowed (full name like "org/repo"). Empty = all in allowed orgs.
    /// </summary>
    [Id(5)]
    public List<string> AllowedRepos { get; set; } = [];

    /// <summary>
    /// Priority-ordered label→profile mappings. First match wins.
    /// </summary>
    [Id(6)]
    public List<LabelProfileMapping> Mappings { get; set; } = [];

    /// <summary>
    /// Fallback profile if no label mapping matches.
    /// </summary>
    [Id(7)]
    public string? DefaultProfileId { get; set; }

    /// <summary>
    /// HMAC-SHA256 secret for webhook signature validation.
    /// </summary>
    [Id(8)]
    public string WebhookSecret { get; set; } = "";

    [Id(9)]
    public int MaxConcurrentJobs { get; set; } = 10;
    [Id(10)]
    public int CooldownSeconds { get; set; } = 5;

    [Id(11)]
    public bool Enabled { get; set; } = true;
    [Id(12)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(13)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Maps a set of required labels to a runner profile.
/// If all RequiredLabels are present in the job's runs-on labels, this mapping matches.
/// </summary>
[GenerateSerializer]
public class LabelProfileMapping
{
    [Id(0)]
    public List<string> RequiredLabels { get; set; } = [];
    [Id(1)]
    public string ProfileId { get; set; } = "";

    /// <summary>Higher priority = checked first.</summary>
    [Id(2)]
    public int Priority { get; set; } = 0;

    public bool Matches(IEnumerable<string> labels)
        => MatchesRequiredLabels(RequiredLabels, labels);

    public static bool MatchesRequiredLabels(IEnumerable<string> requiredLabels, IEnumerable<string> labels)
    {
        var patterns = requiredLabels
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        var labelList = labels
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (patterns.Count == 0 || labelList.Count == 0)
            return false;

        return patterns.All(pattern => labelList.Any(label => MatchesPattern(pattern, label)));
    }

    private static bool MatchesPattern(string pattern, string label)
    {
        pattern = pattern.Trim();
        label = label.Trim();

        if (pattern == "*")
            return true;

        if (!pattern.Contains('*'))
            return string.Equals(pattern, label, StringComparison.OrdinalIgnoreCase);

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(label, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
