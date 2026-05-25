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
    public int MaxConcurrent { get; set; } = 0;
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
    [Id(25)]
    public List<RunnerDefinition> RunnerDefinitions { get; set; } = [];

    public string? ResolveWebhookProfileId(IEnumerable<string> labels, string? preferredProfileId = null)
    {
        var runnerDefinition = ResolveWebhookRunnerDefinition(labels, preferredProfileId);
        if (runnerDefinition != null)
            return runnerDefinition.Id;

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

    public RunnerDefinition? ResolveRunnerDefinition(IEnumerable<string>? labels = null, string? preferredRunnerDefinitionId = null)
    {
        if (Type == ProvisioningType.Webhook)
            return ResolveWebhookRunnerDefinition(labels ?? [], preferredRunnerDefinitionId);

        return RunnerDefinitions
            .Where(r => r.Enabled)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public RunnerDefinition? ResolveWebhookRunnerDefinition(IEnumerable<string> labels, string? preferredRunnerDefinitionId = null)
    {
        var runners = RunnerDefinitions
            .Where(r => r.Enabled)
            .ToList();

        if (runners.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredRunnerDefinitionId))
        {
            var preferred = runners.FirstOrDefault(r =>
                string.Equals(r.Id, preferredRunnerDefinitionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.TargetKey, preferredRunnerDefinitionId, StringComparison.OrdinalIgnoreCase));

            if (preferred != null)
                return preferred;
        }

        var requestedTargetKey = ResolveRequestedTargetKey(labels);
        if (!string.IsNullOrWhiteSpace(requestedTargetKey))
        {
            return runners.FirstOrDefault(runner =>
                string.Equals(runner.TargetKey, requestedTargetKey, StringComparison.OrdinalIgnoreCase));
        }

        var legacyMatcherMatch = runners
            .Select(runner => new
            {
                Runner = runner,
                Matcher = runner.Matchers
                    .Where(m => m.Enabled)
                    .OrderByDescending(m => m.Priority)
                    .FirstOrDefault(m => m.Matches(labels))
            })
            .Where(x => x.Matcher != null)
            .OrderByDescending(x => x.Matcher!.Priority)
            .ThenBy(x => x.Runner.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Runner)
            .FirstOrDefault();

        if (legacyMatcherMatch != null)
            return legacyMatcherMatch;

        return runners.Count == 1 ? runners[0] : null;
    }

    public string? ResolveRequestedTargetKey(IEnumerable<string> labels)
    {
        var cleanLabels = labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .ToList();

        var validTargets = GetValidRunnerTargetKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactMatch = cleanLabels.FirstOrDefault(validTargets.Contains);
        if (!string.IsNullOrWhiteSpace(exactMatch))
            return RunnerDefinition.NormalizeTargetKey(exactMatch);

        return cleanLabels.FirstOrDefault(RunnerDefinition.IsTargetLabelCandidate);
    }

    public List<string> GetValidRunnerTargetKeys() =>
        [.. RunnerDefinitions
            .Where(runner => runner.Enabled)
            .Select(runner => runner.TargetKey)
            .Where(targetKey => !string.IsNullOrWhiteSpace(targetKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(targetKey => targetKey, StringComparer.OrdinalIgnoreCase)];

    public string BuildNoRunnerTargetMatchReason(IEnumerable<string> labels)
    {
        var requestedTargetKey = ResolveRequestedTargetKey(labels);
        var validTargets = GetValidRunnerTargetKeys();
        var validDisplay = validTargets.Count == 0 ? "none configured" : string.Join(", ", validTargets);

        if (!string.IsNullOrWhiteSpace(requestedTargetKey))
            return $"No runner target '{requestedTargetKey}' exists in provisioning rule '{Name}'. Valid targets: {validDisplay}.";

        return $"No runner target was requested by labels [{string.Join(", ", labels)}]. Valid targets: {validDisplay}.";
    }

    public bool IsMissingRunnerTargetRequest(IEnumerable<string> labels) =>
        RunnerDefinitions.Count > 0 && string.IsNullOrWhiteSpace(ResolveRequestedTargetKey(labels));

    public IEnumerable<string> GetRunnerProfileIds()
    {
        if (RunnerDefinitions.Count > 0)
            return RunnerDefinitions
                .Where(r => r.Enabled)
                .Select(r => r.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        if (Type == ProvisioningType.Webhook)
        {
            return LabelMappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ProfileId))
                .Select(mapping => mapping.ProfileId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(ProfileId))
            return [ProfileId.Trim()];

        return [];
    }

    public IEnumerable<RunnerProfile> MaterializeRunnerProfiles(IEnumerable<RunnerInitStep>? initSteps = null)
    {
        if (RunnerDefinitions.Count == 0)
            return [];

        return RunnerDefinitions
            .Where(runner => runner.Enabled)
            .Select(runner => runner.ToProfile(this, initSteps));
    }

    private static bool RunnerMatchesWebhookLabels(RunnerDefinition runner, IEnumerable<string> labels) =>
        runner.Matchers.Any(m => m.Enabled && m.Matches(labels));
}

public enum ProvisioningType
{
    Static,
    ScaleSet,
    Webhook,
    Scheduled
}
