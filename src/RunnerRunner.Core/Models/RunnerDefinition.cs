using Orleans;

namespace RunnerRunner.Core.Models;

/// <summary>
/// Rule-owned runner configuration. This replaces runtime use of global profiles for
/// provisioning rules while keeping reusable dependencies (credentials, env sets,
/// registry credentials, init steps) referenced explicitly.
/// </summary>
[GenerateSerializer]
public class RunnerDefinition
{
    [NonSerialized]
    private string? targetKey;

    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public string? Description { get; set; }
    [Id(3)]
    public bool Enabled { get; set; } = true;
    [Id(4)]
    public HostPlatform RequiredHostPlatform { get; set; }
    [Id(5)]
    public ExecutionBackend ExecutionBackend { get; set; }
    [Id(6)]
    public string? RunnerAgentVersion { get; set; }
    [Id(7)]
    public List<string> EnvironmentVariableSetIds { get; set; } = [];
    [Id(8)]
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    [Id(9)]
    public HashSet<string> EnvironmentOverrideSecretKeys { get; set; } = [];
    [Id(10)]
    public DockerImageConfig? DockerConfig { get; set; }
    [Id(11)]
    public TartImageConfig? TartConfig { get; set; }
    [Id(12)]
    public List<string> Labels { get; set; } = [];
    [Id(13)]
    public string RunnerGroup { get; set; } = "Default";
    [Id(14)]
    public bool Ephemeral { get; set; } = true;
    [Id(15)]
    public string TargetKey
    {
        get => string.IsNullOrWhiteSpace(targetKey) ? GenerateTargetKey(Name) : NormalizeTargetKey(targetKey);
        set => targetKey = NormalizeTargetKey(value);
    }
    [Id(16)]
    public Dictionary<string, string> ProviderConfig { get; set; } = new();
    [Id(17)]
    public bool EmitMetadataLabels { get; set; } = true;
    [Id(18)]
    public bool EmitJobStartedBanner { get; set; } = true;
    [Id(19)]
    public bool AllowWebhookImageTagOverride { get; set; }
    [Id(20)]
    public List<RunnerLabelMatcher> Matchers { get; set; } = [];
    [Id(21)]
    public List<RunnerInitStepRef> InitStepRefs { get; set; } = [];
    [Id(22)]
    public List<RunnerInitStep> InlineInitSteps { get; set; } = [];
    [Id(23)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(24)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public RunnerProfile ToProfile(ProvisioningRule rule, IEnumerable<RunnerInitStep>? initSteps = null)
    {
        return new RunnerProfile
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Provider = rule.Provider,
            ProviderCredentialId = rule.ProviderCredentialId,
            RunnerAgentVersion = RunnerAgentVersion,
            RequiredHostPlatform = RequiredHostPlatform,
            ExecutionBackend = ExecutionBackend,
            EnvironmentVariableSetIds = [.. EnvironmentVariableSetIds],
            EnvironmentOverrides = new Dictionary<string, string>(EnvironmentOverrides),
            EnvironmentOverrideSecretKeys = [.. EnvironmentOverrideSecretKeys],
            DockerConfig = DockerConfig,
            TartConfig = TartConfig,
            Labels = BuildProfileLabels(),
            RunnerGroup = RunnerGroup,
            Ephemeral = Ephemeral,
            ProviderConfig = new Dictionary<string, string>(ProviderConfig),
            EmitMetadataLabels = EmitMetadataLabels,
            EmitJobStartedBanner = EmitJobStartedBanner,
            InitSteps = initSteps?.Select(CloneInitStep).ToList() ?? [.. InlineInitSteps.Select(CloneInitStep)],
            AllowWebhookImageTagOverride = AllowWebhookImageTagOverride,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    public static RunnerDefinition FromProfile(RunnerProfile profile, string? id = null)
    {
        var targetKey = profile.Labels.FirstOrDefault(IsTargetLabelCandidate)
            ?? GenerateTargetKey(profile.Name);

        return new RunnerDefinition
        {
            Id = string.IsNullOrWhiteSpace(id) ? profile.Id : id,
            Name = profile.Name,
            TargetKey = targetKey,
            Description = profile.Description,
            RequiredHostPlatform = profile.RequiredHostPlatform,
            ExecutionBackend = profile.ExecutionBackend,
            RunnerAgentVersion = profile.RunnerAgentVersion,
            EnvironmentVariableSetIds = [.. profile.EnvironmentVariableSetIds],
            EnvironmentOverrides = new Dictionary<string, string>(profile.EnvironmentOverrides),
            EnvironmentOverrideSecretKeys = [.. profile.EnvironmentOverrideSecretKeys],
            DockerConfig = profile.DockerConfig,
            TartConfig = profile.TartConfig,
            Labels = [.. profile.Labels.Where(label => !string.Equals(label, targetKey, StringComparison.OrdinalIgnoreCase))],
            RunnerGroup = profile.RunnerGroup,
            Ephemeral = profile.Ephemeral,
            ProviderConfig = new Dictionary<string, string>(profile.ProviderConfig),
            EmitMetadataLabels = profile.EmitMetadataLabels,
            EmitJobStartedBanner = profile.EmitJobStartedBanner,
            AllowWebhookImageTagOverride = profile.AllowWebhookImageTagOverride,
            InlineInitSteps = [.. profile.InitSteps.Select(CloneInitStep)],
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt
        };
    }

    public void EnsureTargetKey()
    {
        if (string.IsNullOrWhiteSpace(targetKey))
            targetKey = GenerateTargetKey(Name);
    }

    public static string GenerateTargetKey(string? value)
    {
        var normalized = NormalizeTargetKey(value);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "runner";

        return normalized.StartsWith("rr-", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"rr-{normalized}";
    }

    public static string NormalizeTargetKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var chars = new List<char>(value.Length);
        var lastWasSeparator = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_')
            {
                chars.Add(ch);
                lastWasSeparator = false;
            }
            else if (ch is '-' or ' ' or '\t' or '\r' or '\n')
            {
                if (!lastWasSeparator && chars.Count > 0)
                {
                    chars.Add('-');
                    lastWasSeparator = true;
                }
            }
            else if (!lastWasSeparator && chars.Count > 0)
            {
                chars.Add('-');
                lastWasSeparator = true;
            }
        }

        return new string(chars.ToArray()).Trim('-', '.', '_');
    }

    public static bool IsTargetLabelCandidate(string? value)
    {
        var normalized = NormalizeTargetKey(value);
        return !string.IsNullOrWhiteSpace(value)
            && normalized == value.Trim().ToLowerInvariant()
            && normalized.StartsWith("rr-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReservedTargetKey(string? value)
    {
        var normalized = NormalizeTargetKey(value);
        return normalized.StartsWith("rr-image-tag", StringComparison.OrdinalIgnoreCase);
    }

    private List<string> BuildProfileLabels()
    {
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? label)
        {
            var trimmed = label?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
                labels.Add(trimmed);
        }

        Add(TargetKey);
        foreach (var label in Labels)
            Add(label);

        return labels;
    }

    public static RunnerInitStep CloneInitStep(RunnerInitStep step) => new()
    {
        Id = step.Id,
        Name = step.Name,
        Phase = step.Phase,
        Shell = step.Shell,
        Script = step.Script,
        ContinueOnError = step.ContinueOnError,
        TimeoutSeconds = step.TimeoutSeconds,
        WorkingDirectory = step.WorkingDirectory,
        EnvironmentVariableSetIds = [.. step.EnvironmentVariableSetIds],
        EnvironmentOverrides = new Dictionary<string, string>(step.EnvironmentOverrides),
        EnvironmentOverrideSecretKeys = [.. step.EnvironmentOverrideSecretKeys],
        Enabled = step.Enabled
    };
}

[GenerateSerializer]
public class RunnerLabelMatcher
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public List<string> RequiredLabels { get; set; } = [];
    [Id(2)]
    public int Priority { get; set; }
    [Id(3)]
    public bool Enabled { get; set; } = true;

    public bool Matches(IEnumerable<string> labels) =>
        Enabled && LabelProfileMapping.MatchesRequiredLabels(RequiredLabels, labels);
}

[GenerateSerializer]
public class RunnerInitStepDefinition
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public string? Description { get; set; }
    [Id(3)]
    public InitStepPhase Phase { get; set; } = InitStepPhase.PreRunner;
    [Id(4)]
    public InitStepShell Shell { get; set; } = InitStepShell.Auto;
    [Id(5)]
    public string Script { get; set; } = "";
    [Id(6)]
    public bool ContinueOnError { get; set; }
    [Id(7)]
    public int TimeoutSeconds { get; set; } = 600;
    [Id(8)]
    public string? WorkingDirectory { get; set; }
    [Id(9)]
    public List<string> EnvironmentVariableSetIds { get; set; } = [];
    [Id(10)]
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    [Id(11)]
    public HashSet<string> EnvironmentOverrideSecretKeys { get; set; } = [];
    [Id(12)]
    public bool Enabled { get; set; } = true;
    [Id(13)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(14)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public RunnerInitStep ToInitStep(RunnerInitStepRef? reference = null)
    {
        var enabled = reference?.EnabledOverride ?? Enabled;
        var timeout = reference?.TimeoutSecondsOverride ?? TimeoutSeconds;
        var step = new RunnerInitStep
        {
            Id = Id,
            Name = Name,
            Phase = Phase,
            Shell = Shell,
            Script = Script,
            ContinueOnError = ContinueOnError,
            TimeoutSeconds = timeout,
            WorkingDirectory = WorkingDirectory,
            EnvironmentVariableSetIds = [.. EnvironmentVariableSetIds],
            EnvironmentOverrides = new Dictionary<string, string>(EnvironmentOverrides),
            EnvironmentOverrideSecretKeys = [.. EnvironmentOverrideSecretKeys],
            Enabled = enabled
        };

        if (reference is null)
            return step;

        foreach (var kvp in reference.EnvironmentOverrides)
            step.EnvironmentOverrides[kvp.Key] = kvp.Value;
        foreach (var key in reference.EnvironmentOverrideSecretKeys)
            step.EnvironmentOverrideSecretKeys.Add(key);

        return step;
    }
}

[GenerateSerializer]
public class RunnerInitStepRef
{
    [Id(0)]
    public string InitStepId { get; set; } = "";
    [Id(1)]
    public int Order { get; set; }
    [Id(2)]
    public bool? EnabledOverride { get; set; }
    [Id(3)]
    public int? TimeoutSecondsOverride { get; set; }
    [Id(4)]
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    [Id(5)]
    public HashSet<string> EnvironmentOverrideSecretKeys { get; set; } = [];
}
