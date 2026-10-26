using NSubstitute;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Grains;

[Collection(OrleansClusterCollection.Name)]
public class PreparedRegistrationTests(OrleansTestClusterFixture fixture)
{
    [Fact]
    public void PreparedRegistrationRequiresExplicitProfileOptIn()
    {
        var profile = new RunnerProfile { Name = "existing", Provider = RunnerProvider.GitHubActions,
            ExecutionBackend = ExecutionBackend.Docker, DockerConfig = new() { RegistryUrl = "", ImageName = "runner" } };
        Assert.False(DeferredRunnerRegistration.Required(profile, HostPlatform.Windows));
        profile.DockerConfig.PrepareBeforeRegistration = true;
        Assert.True(DeferredRunnerRegistration.Required(profile, HostPlatform.Windows));
        Assert.False(DeferredRunnerRegistration.Required(profile, HostPlatform.Linux));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessIsHostBound_AndConcurrentDeliveryMintsExactlyOnce(bool lostActivationReply)
    {
        var id = OrleansTestIds.Create("prepared");
        var hostId = id + "-host";
        var profileId = id + "-profile";
        var credentialId = id + "-credential";
        var host = fixture.GrainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register("prepared-host", HostPlatform.Windows, "x64", "test");
        await fixture.GrainFactory.GetGrain<IProfileGrain>(profileId).SetProfile(new RunnerProfile
        { Id = profileId, Name = "prepared", Provider = RunnerProvider.GitHubActions, ExecutionBackend = ExecutionBackend.Docker });
        await fixture.DocumentStore.Insert(new ProviderCredential
        { Id = credentialId, Name = "fixture", Provider = RunnerProvider.GitHubActions,
          GitHubToken = "private-token-test", GitHubOrg = id, GitHubApiUrl = "https://fixture.invalid" });
        var runner = fixture.GrainFactory.GetGrain<IRunnerInstanceGrain>(id);
        await runner.Initialize(hostId, profileId, "prepared-runner", "static");
        await runner.MarkStarting();
        await runner.ConfigureDeferredRegistration(new DeferredRunnerRegistration
        { CredentialId = credentialId, Labels = ["self-hosted", "windows"] });

        var receipt = new RunnerStartedEvent
        { InstanceId = id, RunnerName = "prepared-runner", InstanceHandle = "original-container", Backend = ExecutionBackend.Docker };
        var endpoint = $"/orgs/{id}/actions/runners/generate-jitconfig";
        Assert.DoesNotContain(fixture.RegistrationApi.Requests, r => r.PathAndQuery == endpoint);
        await runner.ActivatePrepared("foreign-host", receipt);
        await runner.ActivatePrepared(hostId, receipt); // Reservation has not accepted this native handle.
        Assert.False((await runner.GetState()).RegistrationClaimed);
        Assert.DoesNotContain(fixture.RegistrationApi.Requests, r => r.PathAndQuery == endpoint);

        fixture.Admission.RecordStartedAsync(hostId, id, ExecutionBackend.Docker, "original-container", default).Returns(true);
        if (lostActivationReply)
        {
            fixture.HostCommands.LoseActivationReplyFor(hostId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ActivatePrepared(hostId, receipt));
            Assert.Contains("uncertain", (await runner.GetState()).StatusMessage);
            Assert.DoesNotContain(fixture.HostCommands.Commands, c => c.HostId == hostId && c.Kind == HostCommandKind.StopRunner);
        }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => runner.ActivatePrepared(hostId, receipt)));
        Assert.Single(fixture.RegistrationApi.Requests.Where(r => r.PathAndQuery == endpoint));
        var activation = Assert.Single(fixture.HostCommands.Commands
            .Where(c => c.HostId == hostId && c.Kind == HostCommandKind.ActivateRunner));
        var payload = Assert.IsType<ActivateRunnerCommand>(activation.Command);
        Assert.Equal(id, payload.InstanceId);
        Assert.Equal("original-container", payload.InstanceHandle);
        Assert.Equal("private-jit-test", payload.JitConfig);
        var state = await runner.GetState();
        Assert.True(state.RegistrationClaimed);
        Assert.Null(state.JitConfig);
        Assert.Equal(RunnerInstanceStatus.Starting, state.Status); // Native activation receipt still required.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ConfigureDeferredRegistration(new DeferredRunnerRegistration
        { CredentialId = credentialId }));
    }
}
