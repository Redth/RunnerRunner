using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.TestSupport;

internal sealed class RunnerScenarioBuilder
{
    private readonly TestClock _clock;
    private string _idPrefix = "scenario";
    private HostPlatform _platform = HostPlatform.Linux;
    private ExecutionBackend _backend = ExecutionBackend.Docker;
    private RunnerInstanceStatus _runnerStatus = RunnerInstanceStatus.Pending;
    private string _provisioningMode = "static";
    private string _webhookStatus = "pending";
    private string _jobId = "job-1";
    private int _desiredCount = 1;
    private List<string> _labels = ["self-hosted", "linux"];

    public RunnerScenarioBuilder(TestClock? clock = null)
    {
        _clock = clock ?? new TestClock();
    }

    public RunnerScenarioBuilder WithIds(string idPrefix)
    {
        _idPrefix = idPrefix;
        return this;
    }

    public RunnerScenarioBuilder WithHostPlatform(HostPlatform platform)
    {
        _platform = platform;
        return this;
    }

    public RunnerScenarioBuilder WithExecutionBackend(ExecutionBackend backend)
    {
        _backend = backend;
        return this;
    }

    public RunnerScenarioBuilder WithRunnerStatus(RunnerInstanceStatus status)
    {
        _runnerStatus = status;
        return this;
    }

    public RunnerScenarioBuilder WithDesiredCount(int desiredCount)
    {
        _desiredCount = desiredCount;
        return this;
    }

    public RunnerScenarioBuilder WithLabels(params string[] labels)
    {
        _labels = labels.ToList();
        return this;
    }

    public RunnerScenarioBuilder WithDynamicWebhook(string jobId = "job-1", string status = "provisioned")
    {
        _provisioningMode = "dynamic";
        _jobId = jobId;
        _webhookStatus = status;
        return this;
    }

    public RunnerScenario Build()
    {
        var hostId = $"{_idPrefix}-host";
        var profileId = $"{_idPrefix}-profile";
        var instanceId = $"{_idPrefix}-runner";
        var eventId = $"{_idPrefix}-event";
        var now = _clock.UtcNow;

        var host = new Host
        {
            Id = hostId,
            Name = $"{_idPrefix}-host",
            Platform = _platform,
            AgentStatus = AgentStatus.Online,
            Architecture = _platform == HostPlatform.MacOS ? "arm64" : "x64",
            Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["os"] = _platform.ToString().ToLowerInvariant(),
                ["pool"] = "default"
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var profile = new RunnerProfile
        {
            Id = profileId,
            Name = $"{_idPrefix}-profile",
            Provider = RunnerProvider.GitHubActions,
            RequiredHostPlatform = _platform,
            ExecutionBackend = _backend,
            Labels = _labels.ToList(),
            CreatedAt = now,
            UpdatedAt = now
        };

        var assignment = new RunnerAssignment
        {
            Id = $"{_idPrefix}-assignment",
            HostId = hostId,
            ProfileId = profileId,
            DesiredCount = _desiredCount,
            CreatedAt = now,
            UpdatedAt = now
        };

        var instance = new RunnerInstance
        {
            Id = instanceId,
            HostId = hostId,
            ProfileId = profileId,
            RunnerName = $"{_idPrefix}-runner",
            Status = _runnerStatus,
            ProvisioningMode = _provisioningMode,
            WebhookEventId = _provisioningMode == "dynamic" ? eventId : null,
            JobId = _provisioningMode == "dynamic" ? _jobId : null,
            CreatedAt = now
        };

        var webhook = new WebhookEvent
        {
            Id = eventId,
            BindingId = $"{_idPrefix}-rule",
            Provider = RunnerProvider.GitHubActions.ToString(),
            Action = "queued",
            JobId = _jobId,
            RunId = $"{_idPrefix}-run",
            Repository = "org/repo",
            Labels = _labels.ToList(),
            MatchedProfileId = profileId,
            MatchedProfileName = profile.Name,
            InstanceId = _provisioningMode == "dynamic" ? instanceId : null,
            Status = _webhookStatus,
            ReceivedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            NextRetryAt = now
        };

        return new RunnerScenario(host, profile, assignment, instance, webhook);
    }
}

internal sealed record RunnerScenario(
    Host Host,
    RunnerProfile Profile,
    RunnerAssignment Assignment,
    RunnerInstance Instance,
    WebhookEvent WebhookEvent);
