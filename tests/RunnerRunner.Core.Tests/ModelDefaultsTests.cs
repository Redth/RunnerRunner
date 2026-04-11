using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Tests;

public class ModelDefaultsTests
{
    [Fact]
    public void Host_HasGeneratedId()
    {
        var host = new Host { Name = "test-host" };
        Assert.False(string.IsNullOrEmpty(host.Id));
        Assert.True(Guid.TryParse(host.Id, out _));
    }

    [Fact]
    public void Host_DefaultValues()
    {
        var host = new Host { Name = "test" };
        Assert.Equal(AgentStatus.Offline, host.AgentStatus);
        Assert.Empty(host.Capabilities);
        Assert.Empty(host.EnvironmentOverrides);
        Assert.False(host.IsApproved);
        Assert.Null(host.LastHeartbeat);
    }

    [Fact]
    public void RunnerProfile_DefaultValues()
    {
        var profile = new RunnerProfile { Name = "test" };
        Assert.False(string.IsNullOrEmpty(profile.Id));
        Assert.Equal("Default", profile.RunnerGroup);
        Assert.Equal(1, profile.MaxParallelPerHost);
        Assert.False(profile.Ephemeral);
        Assert.Empty(profile.Labels);
        Assert.Empty(profile.EnvironmentVariableSetIds);
        Assert.Empty(profile.EnvironmentOverrides);
        Assert.Empty(profile.ProviderConfig);
        Assert.Null(profile.DockerConfig);
        Assert.Null(profile.TartConfig);
        Assert.Null(profile.RunnerAgentVersion);
    }

    [Fact]
    public void RunnerInstance_DefaultValues()
    {
        var instance = new RunnerInstance { RunnerName = "test-runner" };
        Assert.False(string.IsNullOrEmpty(instance.Id));
        Assert.Equal(RunnerInstanceStatus.Pending, instance.Status);
        Assert.Null(instance.StartedAt);
        Assert.Null(instance.StoppedAt);
        Assert.Null(instance.ContainerId);
        Assert.Null(instance.ErrorMessage);
    }

    [Fact]
    public void EnvironmentVariableSet_DefaultValues()
    {
        var evs = new EnvironmentVariableSet { Name = "test" };
        Assert.False(string.IsNullOrEmpty(evs.Id));
        Assert.Empty(evs.Variables);
        Assert.Equal(0, evs.Priority);
    }

    [Fact]
    public void RunnerAssignment_DefaultValues()
    {
        var assignment = new RunnerAssignment();
        Assert.False(string.IsNullOrEmpty(assignment.Id));
        Assert.Equal(1, assignment.DesiredCount);
        Assert.Equal("", assignment.HostId);
        Assert.Equal("", assignment.ProfileId);
    }

    [Fact]
    public void DockerImageConfig_DefaultValues()
    {
        var config = new DockerImageConfig { RegistryUrl = "ghcr.io", ImageName = "test" };
        Assert.Equal("latest", config.Tag);
        Assert.Equal(PullPolicy.IfNotPresent, config.PullPolicy);
    }

    [Fact]
    public void TartImageConfig_DefaultValues()
    {
        var config = new TartImageConfig { RegistryUrl = "ghcr.io", ImageName = "test" };
        Assert.Equal("latest", config.Tag);
        Assert.Null(config.CpuCount);
        Assert.Null(config.MemorySizeGb);
        Assert.Null(config.DiskSizeGb);
        Assert.Empty(config.SharedDirs);
    }

    [Fact]
    public void ProviderCredential_DefaultValues()
    {
        var cred = new ProviderCredential { Name = "test" };
        Assert.False(string.IsNullOrEmpty(cred.Id));
        Assert.Null(cred.GitHubOrg);
        Assert.Null(cred.GitHubToken);
        Assert.Null(cred.GiteaInstanceUrl);
        Assert.Null(cred.AzDoOrgUrl);
    }

    [Fact]
    public void AuditLogEntry_DefaultTimestamp()
    {
        var before = DateTime.UtcNow;
        var entry = new AuditLogEntry { Action = "test", EntityType = "test" };
        Assert.True(entry.Timestamp >= before);
        Assert.True(entry.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public void RunnerAgentVersion_DefaultValues()
    {
        var version = new RunnerAgentVersion { Version = "2.300.0" };
        Assert.False(version.IsLatest);
        Assert.Null(version.DownloadUrlLinuxX64);
    }

    [Fact]
    public void UniqueIds_AcrossInstances()
    {
        var ids = Enumerable.Range(0, 100)
            .Select(_ => new Host { Name = "test" }.Id)
            .ToHashSet();
        Assert.Equal(100, ids.Count);
    }
}
