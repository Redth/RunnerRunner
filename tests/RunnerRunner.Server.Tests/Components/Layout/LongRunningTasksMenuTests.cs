using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Components.Layout;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Components.Layout;

public class LongRunningTasksMenuTests
{
    [Fact]
    public async Task ShowsRunningTaskBadgeAndHostLabel()
    {
        using var context = new BunitContext();
        using var taskService = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var store = TestDocumentStore.Create();

        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "mac-host-01",
            DisplayName = "Build Mac",
            Platform = HostPlatform.MacOS
        });

        taskService.TrackImagePull("host-1", new PullImageCommand
        {
            ImageType = ImageType.Docker,
            RegistryUrl = "https://ghcr.io",
            ImageName = "runner/agent",
            Tag = "latest"
        });

        AddServices(context, store, taskService);

        var cut = context.Render<LongRunningTasksMenu>();

        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find(".long-tasks-badge").TextContent));

        cut.Find("button.long-tasks-toggle").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("1 task running", cut.Markup);
            Assert.Contains("Pull ghcr.io/runner/agent:latest", cut.Markup);
            Assert.Contains("Build Mac", cut.Markup);
            Assert.Contains("Running", cut.Markup);
        });
    }

    [Fact]
    public async Task ClearCompletedRemovesCompletedTasksFromPopover()
    {
        using var context = new BunitContext();
        using var taskService = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var store = TestDocumentStore.Create();

        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "mac-host-01",
            Platform = HostPlatform.MacOS
        });

        var taskId = taskService.TrackImagePull("host-1", new PullImageCommand
        {
            ImageType = ImageType.Tart,
            ImageName = "ghcr.io/example/macos",
            Tag = "sonoma"
        });
        taskService.MarkFailed(taskId, "Image pull failed.");

        AddServices(context, store, taskService);

        var cut = context.Render<LongRunningTasksMenu>();
        cut.Find("button.long-tasks-toggle").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Failed", cut.Markup);
            Assert.Contains("Image pull failed.", cut.Markup);
        });

        cut.Find("button.btn-link").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No running tasks.", cut.Markup);
            Assert.Empty(taskService.GetSnapshot());
        });
    }

    private static void AddServices(BunitContext context, IDocumentStore store, LongRunningTaskService taskService)
    {
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(taskService);
    }
}
