using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class DynamicProvisioningServiceTests
{
    [Fact]
    public void BuildDynamicRunnerLabels_MergesWebhookAndProfileLabels_DistinctCaseInsensitive()
    {
        var evt = new WebhookEvent
        {
            Labels = ["self-hosted", "linux", "tests"]
        };

        var profile = new RunnerProfile
        {
            Name = "linux-tests",
            Labels = ["Linux", "docker", "self-hosted"],
            EmitMetadataLabels = false // isolate the merge/dedup behavior
        };

        var result = DynamicProvisioningService.BuildDynamicRunnerLabels(evt, profile);

        Assert.Equal(["self-hosted", "linux", "tests", "docker"], result);
    }

    [Fact]
    public void BuildDynamicRunnerLabels_AppendsMetadataLabels_WhenProfileOptsIn()
    {
        var evt = new WebhookEvent { Labels = ["self-hosted"] };
        var profile = new RunnerProfile
        {
            Name = "linux-tests",
            ExecutionBackend = ExecutionBackend.Docker,
            Provider = RunnerProvider.GitHubActions,
            Labels = [],
            DockerConfig = new DockerImageConfig
            {
                RegistryUrl = "ghcr.io",
                ImageName = "acme/ubuntu-runner",
                Tag = "24.04"
            }
        };
        var host = new RunnerRunner.Core.Models.Host { Name = "mac-studio-01" };

        var result = DynamicProvisioningService.BuildDynamicRunnerLabels(evt, profile, host);

        Assert.Contains("self-hosted", result);
        Assert.Contains("rr-backend:docker", result);
        Assert.Contains("rr-provider:GitHubActions", result);
        Assert.Contains("rr-profile:linux-tests", result);
        Assert.Contains("rr-host:mac-studio-01", result);
        Assert.Contains("rr-image:ghcr.io-acme-ubuntu-runner", result);
        Assert.Contains("rr-tag:24.04", result);
    }

    [Fact]
    public void BuildDynamicRunnerLabels_SuppressesMetadataLabels_WhenProfileOptsOut()
    {
        var evt = new WebhookEvent { Labels = ["self-hosted"] };
        var profile = new RunnerProfile
        {
            Name = "linux-tests",
            ExecutionBackend = ExecutionBackend.Docker,
            EmitMetadataLabels = false,
            Labels = ["custom"]
        };

        var result = DynamicProvisioningService.BuildDynamicRunnerLabels(evt, profile, host: null);

        Assert.DoesNotContain(result, l => l.StartsWith("rr-"));
    }

    [Fact]
    public void BuildGitHubRunQueries_IncludesActiveRecoveryQueries()
    {
        var queries = DynamicProvisioningService.BuildGitHubRunQueries("https://api.github.com", "Redth/ailoha");

        Assert.Contains("https://api.github.com/repos/Redth/ailoha/actions/runs?status=queued&per_page=100", queries);
        Assert.Contains("https://api.github.com/repos/Redth/ailoha/actions/runs?status=in_progress&per_page=100", queries);
        Assert.Contains("https://api.github.com/repos/Redth/ailoha/actions/runs?status=requested&per_page=100", queries);
        Assert.Contains("https://api.github.com/repos/Redth/ailoha/actions/runs?status=waiting&per_page=100", queries);
        // The unfiltered listing is intentionally excluded — it returns completed runs and
        // balloons into per-run job lookups that chew through the GitHub API rate limit.
        Assert.DoesNotContain("https://api.github.com/repos/Redth/ailoha/actions/runs?per_page=100", queries);
    }

    [Fact]
    public void ShouldRetryProvisionedEvent_ReturnsTrueForInvalidStaticLink()
    {
        var evt = new WebhookEvent
        {
            Id = "evt-1",
            Action = "queued",
            Status = "provisioned",
            JobId = "job-123",
            InstanceId = "inst-1"
        };

        var linkedInstance = new RunnerInstance
        {
            Id = "inst-1",
            RunnerName = "bad-runner",
            ProvisioningMode = "static"
        };

        var shouldRetry = DynamicProvisioningService.ShouldRetryProvisionedEvent(evt, linkedInstance, out var reason);

        Assert.True(shouldRetry);
        Assert.Contains("non-dynamic runner", reason);
    }

    [Fact]
    public void ShouldRetryProvisionedEvent_ReturnsFalseForHealthyDynamicLink()
    {
        var evt = new WebhookEvent
        {
            Id = "evt-2",
            Action = "queued",
            Status = "provisioned",
            JobId = "job-456",
            InstanceId = "inst-2"
        };

        var linkedInstance = new RunnerInstance
        {
            Id = "inst-2",
            RunnerName = "dynamic-runner",
            ProvisioningMode = "dynamic",
            JobId = "job-456",
            WebhookEventId = "evt-2",
            Status = RunnerInstanceStatus.Running
        };

        var shouldRetry = DynamicProvisioningService.ShouldRetryProvisionedEvent(evt, linkedInstance, out var reason);

        Assert.False(shouldRetry);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ShouldBlockQueuedGitHubBackfill_IgnoresTimedOutQueuedEvents()
    {
        var evt = new WebhookEvent
        {
            Provider = RunnerProvider.GitHubActions.ToString(),
            Action = "queued",
            Status = "timed_out"
        };

        var shouldBlock = DynamicProvisioningService.ShouldBlockQueuedGitHubBackfill(evt);

        Assert.False(shouldBlock);
    }

    [Fact]
    public void ShouldBlockQueuedGitHubBackfill_KeepsActiveQueuedEvents()
    {
        var evt = new WebhookEvent
        {
            Provider = RunnerProvider.GitHubActions.ToString(),
            Action = "queued",
            Status = "provisioned"
        };

        var shouldBlock = DynamicProvisioningService.ShouldBlockQueuedGitHubBackfill(evt);

        Assert.True(shouldBlock);
    }

    [Fact]
    public void ApplyImageTagOverride_ProfileOptedIn_OverridesDockerTag()
    {
        var docker = new DockerImageConfig
        {
            RegistryUrl = "ghcr.io",
            ImageName = "acme/runner",
            Tag = "default",
            CredentialId = "cred1"
        };
        var profile = new RunnerProfile
        {
            Name = "p",
            AllowWebhookImageTagOverride = true,
            DockerConfig = docker
        };

        var (d, t, applied) = DynamicProvisioningService.ApplyImageTagOverride(profile, "v2025.11");

        Assert.Equal("v2025.11", applied);
        Assert.NotSame(docker, d);
        Assert.Equal("v2025.11", d!.Tag);
        Assert.Equal("ghcr.io", d.RegistryUrl);
        Assert.Equal("acme/runner", d.ImageName);
        Assert.Equal("cred1", d.CredentialId);
        Assert.Null(t);
        // Shared profile config must not be mutated.
        Assert.Equal("default", docker.Tag);
    }

    [Fact]
    public void ApplyImageTagOverride_ProfileNotOptedIn_NoChange()
    {
        var docker = new DockerImageConfig
        {
            RegistryUrl = "ghcr.io",
            ImageName = "acme/runner",
            Tag = "default"
        };
        var profile = new RunnerProfile
        {
            Name = "p",
            AllowWebhookImageTagOverride = false,
            DockerConfig = docker
        };

        var (d, _, applied) = DynamicProvisioningService.ApplyImageTagOverride(profile, "v2025.11");

        Assert.Null(applied);
        Assert.Same(docker, d);
        Assert.Equal("default", d!.Tag);
    }

    [Fact]
    public void ApplyImageTagOverride_NoOverride_NoChange()
    {
        var profile = new RunnerProfile
        {
            Name = "p",
            AllowWebhookImageTagOverride = true,
            DockerConfig = new DockerImageConfig
            {
                RegistryUrl = "ghcr.io",
                ImageName = "acme/runner",
                Tag = "default"
            }
        };

        var (_, _, applied) = DynamicProvisioningService.ApplyImageTagOverride(profile, null);
        Assert.Null(applied);
    }
}
