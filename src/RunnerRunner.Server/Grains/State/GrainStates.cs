using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Grains.State;

[GenerateSerializer]
public class HostGrainState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public string? DisplayName { get; set; }
    [Id(2)] public HostPlatform Platform { get; set; }
    [Id(3)] public string? Architecture { get; set; }
    [Id(4)] public string? AgentVersion { get; set; }
    [Id(5)] public Dictionary<string, string> Labels { get; set; } = new();
    [Id(6)] public string? GroupId { get; set; }

    // Resource limits
    [Id(7)] public int MaxDockerContainers { get; set; } = 10;
    [Id(8)] public int MaxTartVMs { get; set; } = 3;
    [Id(9)] public int MaxNativeProcesses { get; set; } = 5;

    // Current usage
    [Id(10)] public int RunningDockerContainers { get; set; }
    [Id(11)] public int RunningTartVMs { get; set; }
    [Id(12)] public int RunningNativeProcesses { get; set; }

    // Connection state
    [Id(13)] public AgentStatus Status { get; set; } = AgentStatus.Offline;
    [Id(14)] public string? ConnectionId { get; set; }
    [Id(15)] public DateTime? LastHeartbeat { get; set; }
    [Id(16)] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Cached host environment
    [Id(17)] public Dictionary<string, string> ReportedEnvironment { get; set; } = new();
}

[GenerateSerializer]
public class HostGroupGrainState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public string? Description { get; set; }
    [Id(2)] public Dictionary<string, string> SharedLabels { get; set; } = new();
    [Id(3)] public List<string> HostIds { get; set; } = [];
    [Id(4)] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[GenerateSerializer]
public class RunnerInstanceGrainState
{
    [Id(0)] public string HostId { get; set; } = "";
    [Id(1)] public string ProfileId { get; set; } = "";
    [Id(2)] public string RunnerName { get; set; } = "";
    [Id(3)] public RunnerInstanceStatus Status { get; set; } = RunnerInstanceStatus.Pending;

    [Id(4)] public string? ContainerId { get; set; }
    [Id(5)] public string? VmName { get; set; }
    [Id(6)] public int? ProcessId { get; set; }
    [Id(7)] public string? ErrorMessage { get; set; }
    [Id(8)] public string? StatusMessage { get; set; }

    [Id(9)] public string ProvisioningMode { get; set; } = "static";
    [Id(10)] public string? JobId { get; set; }
    [Id(11)] public string? JitConfig { get; set; }
    [Id(12)] public string? WebhookEventId { get; set; }

    [Id(13)] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(14)] public DateTime? DeployedAt { get; set; }
    [Id(15)] public DateTime? StartedAt { get; set; }
    [Id(16)] public DateTime? StoppedAt { get; set; }
    [Id(17)] public DateTime? LastHealthCheck { get; set; }

    // Recent log lines (bounded buffer)
    [Id(18)] public List<string> RecentLogs { get; set; } = [];

    [Id(19)] public ExecutionBackend Backend { get; set; }
}

[GenerateSerializer]
public class ProvisioningRuleGrainState
{
    [Id(0)] public ProvisioningRuleConfig Config { get; set; } = new();
    [Id(1)] public List<string> ManagedInstanceIds { get; set; } = [];
    [Id(2)] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(3)] public DateTime? LastReconciliation { get; set; }
}

[GenerateSerializer]
public class ProvisioningRuleConfig
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public string? Description { get; set; }
    [Id(2)] public string ProfileId { get; set; } = "";
    [Id(3)] public ProvisioningType Type { get; set; } = ProvisioningType.Static;
    [Id(4)] public bool Enabled { get; set; } = true;

    // Static: fixed count
    [Id(5)] public int DesiredCount { get; set; } = 1;
    [Id(6)] public string? TargetHostId { get; set; }

    // ScaleSet: auto-scale bounds
    [Id(7)] public int MinReady { get; set; } = 0;
    [Id(8)] public int MaxInstances { get; set; } = 5;
    [Id(9)] public int ScaleDownDelaySeconds { get; set; } = 300;

    // Webhook: event-triggered
    [Id(10)] public string? WebhookSecret { get; set; }
    [Id(11)] public List<string> AllowedOrgs { get; set; } = [];
    [Id(12)] public List<string> AllowedRepos { get; set; } = [];
    [Id(13)] public List<LabelMappingConfig> LabelMappings { get; set; } = [];
    [Id(14)] public string? DefaultProfileId { get; set; }
    [Id(15)] public int MaxConcurrent { get; set; } = 10;
    [Id(16)] public int CooldownSeconds { get; set; } = 5;

    // Host matching
    [Id(17)] public Dictionary<string, string> RequiredHostLabels { get; set; } = new();
    [Id(18)] public string? TargetGroupId { get; set; }

    // Scheduled (future)
    [Id(19)] public string? CronExpression { get; set; }
}

[GenerateSerializer]
public class LabelMappingConfig
{
    [Id(0)] public List<string> RequiredLabels { get; set; } = [];
    [Id(1)] public string ProfileId { get; set; } = "";
    [Id(2)] public int Priority { get; set; } = 0;
}

[GenerateSerializer]
public class ProfileGrainStateWrapper
{
    [Id(0)] public RunnerProfile? Profile { get; set; }
}
