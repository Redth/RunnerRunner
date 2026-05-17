using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.State;

namespace RunnerRunner.Server.Grains.Interfaces;

/// <summary>
/// Grain representing a host machine that can run runners.
/// Key: Host ID (string).
/// </summary>
public interface IHostGrain : IGrainWithStringKey
{
    Task<HostGrainState> GetState();
    Task Register(string name, HostPlatform platform, string? architecture, string agentVersion);
    Task UpdateLabels(Dictionary<string, string> labels);
    Task SetResourceLimits(int maxDocker, int maxTart, int maxNative);
    Task SetGroupId(string? groupId);
    Task RecordHeartbeat(string connectionId, int runningCount, HostResourceUsage? resourceUsage);
    Task MarkOffline();
    Task<bool> CanAcceptRunner(ExecutionBackend backend);
    Task IncrementRunningCount(ExecutionBackend backend);
    Task DecrementRunningCount(ExecutionBackend backend);
    Task<string?> GetConnectionId();
}

/// <summary>
/// Grain representing a host group (logical grouping of hosts).
/// Key: Group ID (string).
/// </summary>
public interface IHostGroupGrain : IGrainWithStringKey
{
    Task<HostGroupGrainState> GetState();
    Task SetConfig(string name, string? description, Dictionary<string, string> sharedLabels);
    Task AddHost(string hostId);
    Task RemoveHost(string hostId);
    Task<List<string>> GetHostIds();
}

/// <summary>
/// Grain representing a runner instance lifecycle.
/// Key: Instance ID (string).
/// </summary>
public interface IRunnerInstanceGrain : IGrainWithStringKey
{
    Task<RunnerInstanceGrainState> GetState();
    Task Initialize(string hostId, string profileId, string runnerName, string provisioningMode, string? jobId = null, string? webhookEventId = null, string? provisioningRuleId = null, string? imageTagOverride = null);
    Task MarkDeployed();
    Task MarkStarting(string? statusMessage = null);
    Task MarkRunning(string? containerId = null, string? vmName = null, int? processId = null, string? statusMessage = null);
    Task MarkStopping();
    Task MarkStopped();
    Task MarkFailed(string error);
    Task MarkCrashed(string reason);
    Task UpdateHealth(string? statusMessage = null);
    Task UpdateStatusMessage(string message);
    Task DeployLocally(DeployRunnerCommand command);
}

/// <summary>
/// Grain representing a runner profile configuration.
/// Key: Profile ID (string).
/// </summary>
public interface IProfileGrain : IGrainWithStringKey
{
    Task<RunnerProfile?> GetProfile();
    Task SetProfile(RunnerProfile profile);
    Task<Dictionary<string, string>> ComposeEnvironmentVariables();
}

/// <summary>
/// Grain representing a provisioning rule (unified static/scaleset/webhook).
/// Key: Rule ID (string).
/// </summary>
public interface IProvisioningRuleGrain : IGrainWithStringKey
{
    Task<ProvisioningRuleGrainState> GetState();
    Task SetConfig(ProvisioningRuleConfig config);
    Task Enable();
    Task Disable();
    Task Reconcile(); // check desired vs actual and act
    Task HandleWebhookEvent(string jobId, string repo, List<string> labels, string? jitConfig, string? imageTagOverride = null);
    Task HandleJobCompleted(string jobId);
    Task<List<string>> GetManagedInstanceIds();
}

/// <summary>
/// Singleton grain for host selection and load balancing.
/// Key: 0 (integer).
/// </summary>
public interface ISchedulerGrain : IGrainWithIntegerKey
{
    Task<string?> SelectHost(Dictionary<string, string> requiredLabels, ExecutionBackend backend);
    Task RegisterHost(string hostId);
    Task UnregisterHost(string hostId);
}

/// <summary>
/// Stateless worker grain for processing incoming webhooks.
/// </summary>
public interface IWebhookProcessorGrain : IGrainWithIntegerKey
{
    Task<WebhookProcessResult> ProcessWebhook(string provider, string body, string? signatureHeader);
}

[GenerateSerializer]
public class WebhookProcessResult
{
    [Id(0)] public bool Success { get; set; }
    [Id(1)] public string Status { get; set; } = "";
    [Id(2)] public string? Message { get; set; }
    [Id(3)] public string? ProfileId { get; set; }
    [Id(4)] public string? InstanceId { get; set; }
    [Id(5)] public string? EventId { get; set; }
}
