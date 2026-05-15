using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class LongRunningTaskServiceTests
{
    [Fact]
    public void TrackImagePullCreatesRunningTaskAndAssignsTaskId()
    {
        using var service = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var command = new PullImageCommand
        {
            ImageType = ImageType.Docker,
            ImageName = "library/ubuntu",
            Tag = "latest"
        };

        var taskId = service.TrackImagePull("host-1", command);

        var task = Assert.Single(service.GetSnapshot());
        Assert.Equal(taskId, command.TaskId);
        Assert.Equal(taskId, task.Id);
        Assert.Equal(LongRunningTaskStatus.Running, task.Status);
        Assert.Equal(1, service.ActiveCount);
    }

    [Fact]
    public void TrackImagePullFormatsTartRegistryImageWithTag()
    {
        using var service = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var command = new PullImageCommand
        {
            ImageType = ImageType.Tart,
            ImageName = "example/macos",
            RegistryUrl = "https://ghcr.io",
            Tag = "sonoma"
        };

        service.TrackImagePull("host-1", command);

        var task = Assert.Single(service.GetSnapshot());
        Assert.Equal("ghcr.io/example/macos:sonoma", task.Subject);
        Assert.Equal("Pull ghcr.io/example/macos:sonoma", task.Title);
    }

    [Fact]
    public void ImagePullProgressAndCompleteUpdateTrackedTask()
    {
        using var service = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var command = new PullImageCommand
        {
            ImageType = ImageType.Docker,
            ImageName = "library/ubuntu",
            Tag = "latest"
        };
        var taskId = service.TrackImagePull("host-1", command);

        StreamSubscriptionService.PublishImagePullProgress(new ImagePullProgressEvent
        {
            HostId = "host-1",
            ImageType = ImageType.Docker,
            ImageName = "library/ubuntu",
            TaskId = taskId,
            ProgressPercent = 42,
            Status = "Downloading"
        });
        StreamSubscriptionService.PublishImagePullComplete(new ImagePullCompleteEvent
        {
            HostId = "host-1",
            ImageType = ImageType.Docker,
            ImageName = "library/ubuntu",
            TaskId = taskId,
            Success = true
        });

        var task = Assert.Single(service.GetSnapshot());
        Assert.Equal(LongRunningTaskStatus.Succeeded, task.Status);
        Assert.Equal(100, task.ProgressPercent);
        Assert.Equal(0, service.ActiveCount);
    }

    [Fact]
    public void MarkFailedCompletesTaskWithError()
    {
        using var service = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var command = new PullImageCommand
        {
            ImageType = ImageType.Tart,
            ImageName = "ghcr.io/example/macos",
            Tag = "latest"
        };
        var taskId = service.TrackImagePull("host-1", command);

        service.MarkFailed(taskId, "HostWorker is not connected.");

        var task = Assert.Single(service.GetSnapshot());
        Assert.Equal(LongRunningTaskStatus.Failed, task.Status);
        Assert.Equal("HostWorker is not connected.", task.Error);
        Assert.Equal(0, service.ActiveCount);
    }
}
