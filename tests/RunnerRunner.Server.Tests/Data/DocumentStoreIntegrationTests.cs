using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Data;

public class DocumentStoreIntegrationTests
{
    [Fact]
    public async Task CanInsertAndQueryHosts()
    {
        var store = TestDocumentStore.Create();

        var host = new Host
        {
            Name = "test-host",
            Platform = HostPlatform.Linux,
            Capabilities = ["docker", "native"],
            AgentStatus = AgentStatus.Online
        };

        await store.Insert(host);

        var retrieved = await store.Get<Host>(host.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("test-host", retrieved.Name);
        Assert.Equal(HostPlatform.Linux, retrieved.Platform);
        Assert.Contains("docker", retrieved.Capabilities);
    }

    [Fact]
    public async Task CanInsertAndQueryProfiles()
    {
        var store = TestDocumentStore.Create();

        var profile = new RunnerProfile
        {
            Name = "test-profile",
            Provider = RunnerProvider.GitHubActions,
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = ExecutionBackend.Tart,
            Labels = ["macos", "xcode-15"],
            EnvironmentOverrides = new() { ["XCODE_VERSION"] = "15.0" },
            DockerConfig = null,
            TartConfig = new TartImageConfig
            {
                RegistryUrl = "ghcr.io",
                ImageName = "cirruslabs/macos-sequoia-base",
                CpuCount = 4,
                MemorySizeGb = 8
            }
        };

        await store.Insert(profile);

        var retrieved = await store.Get<RunnerProfile>(profile.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("test-profile", retrieved.Name);
        Assert.Equal(RunnerProvider.GitHubActions, retrieved.Provider);
        Assert.NotNull(retrieved.TartConfig);
        Assert.Equal(4, retrieved.TartConfig!.CpuCount);
        Assert.Contains("macos", retrieved.Labels);
        Assert.Equal("15.0", retrieved.EnvironmentOverrides["XCODE_VERSION"]);
    }

    [Fact]
    public async Task CanInsertAndQueryEnvVarSets()
    {
        var store = TestDocumentStore.Create();

        var evs = new EnvironmentVariableSet
        {
            Name = "dotnet-sdk",
            Priority = 5,
            Variables = new()
            {
                ["DOTNET_ROOT"] = "/usr/share/dotnet",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            }
        };

        await store.Insert(evs);

        var result = (await store.Query<EnvironmentVariableSet>()
            .Where(e => e.Name == "dotnet-sdk")
            .ToList()).ToList();

        Assert.Single(result);
        Assert.Equal(5, result[0].Priority);
        Assert.Equal(2, result[0].Variables.Count);
    }

    [Fact]
    public async Task CanUpdateHost()
    {
        var store = TestDocumentStore.Create();

        var host = new Host { Name = "host-1", AgentStatus = AgentStatus.Offline };
        await store.Insert(host);

        host.AgentStatus = AgentStatus.Online;
        host.LastHeartbeat = DateTime.UtcNow;
        await store.Update(host);

        var updated = await store.Get<Host>(host.Id);
        Assert.Equal(AgentStatus.Online, updated!.AgentStatus);
        Assert.NotNull(updated.LastHeartbeat);
    }

    [Fact]
    public async Task CanDeleteRunnerInstance()
    {
        var store = TestDocumentStore.Create();

        var instance = new RunnerInstance { RunnerName = "to-delete" };
        await store.Insert(instance);

        var deleted = await store.Remove<RunnerInstance>(instance.Id);
        Assert.True(deleted);

        var retrieved = await store.Get<RunnerInstance>(instance.Id);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task RunnerAssignment_QueryByHost()
    {
        var store = TestDocumentStore.Create();

        var hostId = Guid.NewGuid().ToString();
        await store.Insert(new RunnerAssignment { HostId = hostId, ProfileId = "p1", DesiredCount = 2 });
        await store.Insert(new RunnerAssignment { HostId = hostId, ProfileId = "p2", DesiredCount = 1 });
        await store.Insert(new RunnerAssignment { HostId = "other-host", ProfileId = "p1", DesiredCount = 5 });

        var assignments = (await store.Query<RunnerAssignment>()
            .Where(a => a.HostId == hostId)
            .ToList()).ToList();

        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, a => Assert.Equal(hostId, a.HostId));
    }

    [Fact]
    public async Task ProviderCredential_RoundTrips()
    {
        var store = TestDocumentStore.Create();

        var cred = new ProviderCredential
        {
            Name = "github-main",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "my-org",
            GitHubToken = "ghp_secret123",
            GitHubAppInstallations =
            [
                new GitHubAppInstallation
                {
                    Owner = "my-org",
                    InstallationId = "123",
                    IsDefault = true
                }
            ]
        };
        await store.Insert(cred);

        var retrieved = await store.Get<ProviderCredential>(cred.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("my-org", retrieved.GitHubOrg);
        Assert.Equal("ghp_secret123", retrieved.GitHubToken);
        Assert.Single(retrieved.GitHubAppInstallations);
        Assert.Equal("123", retrieved.GitHubAppInstallations[0].InstallationId);
    }
}
