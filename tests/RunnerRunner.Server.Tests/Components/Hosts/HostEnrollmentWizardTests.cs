using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Components.Hosts;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Components.Hosts;

public class HostEnrollmentWizardTests
{
    [Fact]
    public void Close_RemovesUnenrolledPendingHost()
    {
        using var context = new BunitContext();
        var store = TestDocumentStore.Create();
        AddServices(context, store);

        var cut = context.Render<HostEnrollmentWizard>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.OnChanged, EventCallback.Factory.Create(this, () => { })));

        cut.WaitForAssertion(() =>
        {
            var host = Assert.Single(ReadHosts(store));
            Assert.StartsWith("pending-", host.Name);
            Assert.False(host.IsApproved);
            Assert.NotNull(host.EnrollmentTokenHash);
        });

        cut.Find("button.btn-close").Click();

        cut.WaitForAssertion(() => Assert.Empty(ReadHosts(store)));
    }

    [Fact]
    public async Task Close_KeepsHostThatEnrolledBeforeCleanup()
    {
        using var context = new BunitContext();
        var store = TestDocumentStore.Create();
        AddServices(context, store);

        var cut = context.Render<HostEnrollmentWizard>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.OnChanged, EventCallback.Factory.Create(this, () => { })));

        cut.WaitForState(() => ReadHosts(store).Count == 1);
        var pendingHost = Assert.Single(ReadHosts(store));
        pendingHost.IsApproved = true;
        pendingHost.EnrolledAt = DateTime.UtcNow;
        pendingHost.WorkerId = "worker-1";
        await store.Update(pendingHost);

        cut.Find("button.btn-close").Click();

        cut.WaitForAssertion(() =>
        {
            var host = Assert.Single(ReadHosts(store));
            Assert.True(host.IsApproved);
            Assert.Equal("worker-1", host.WorkerId);
        });
    }

    [Fact]
    public void SelectWsl2Target_ExplainsLinuxAndArm64Behavior()
    {
        using var context = new BunitContext();
        var store = TestDocumentStore.Create();
        AddServices(context, store);

        var cut = context.Render<HostEnrollmentWizard>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.OnChanged, EventCallback.Factory.Create(this, () => { })));

        cut.FindAll("button.enrollment-target-card")
            .Single(button => button.TextContent.Contains("Windows + WSL2"))
            .Click();

        var callout = cut.Find(".wsl2-enrollment-callout");
        Assert.Contains("appear as Linux", callout.TextContent);
        Assert.Contains("select ARM64 automatically", callout.TextContent);
    }

    private static IReadOnlyList<Host> ReadHosts(IDocumentStore store)
        => store.Query<Host>().ToList().GetAwaiter().GetResult();

    private static void AddServices(BunitContext context, IDocumentStore store)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorkerUpdates:LocalArtifactRoot"] = Path.Combine(Path.GetTempPath(), $"rr-hostworker-updates-{Guid.NewGuid():N}")
            })
            .Build();

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(Path.GetTempPath());

        var localUpdateStore = new HostWorkerLocalUpdateStore(configuration, environment);
        var gitHubAuth = new GitHubAuthenticationService(
            httpClientFactory,
            NullLogger<GitHubAuthenticationService>.Instance);
        var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var updateService = new HostWorkerUpdateService(
            httpClientFactory,
            configuration,
            gitHubAuth,
            store,
            Substitute.For<IHostCommandDispatcher>(),
            Substitute.For<IGrainFactory>(),
            localUpdateStore,
            tasks,
            NullLogger<HostWorkerUpdateService>.Instance);

        context.Services.AddSingleton(store);
        context.Services.AddSingleton(tasks);
        context.Services.AddSingleton(new HostWorkerEnrollmentGuideBuilder(configuration));
        context.Services.AddSingleton(updateService);
        context.Services.AddSingleton(new HostWorkerSshSetupService(NullLogger<HostWorkerSshSetupService>.Instance));
    }
}
