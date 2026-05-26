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
    public void ImagePullProgressStoresLayerDetails()
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
            ProgressPercent = 50,
            BytesDownloaded = 50,
            BytesTotal = 100,
            Status = "abc123def456: Downloading 50B/100B",
            Layers =
            [
                new ImagePullLayerProgress
                {
                    Id = "abc123def4567890",
                    Status = "Pull complete",
                    ProgressPercent = 100,
                    BytesDownloaded = 100,
                    BytesTotal = 100,
                    IsComplete = true
                },
                new ImagePullLayerProgress
                {
                    Id = "def456abc123",
                    Status = "Downloading 50B/100B",
                    ProgressPercent = 50,
                    BytesDownloaded = 50,
                    BytesTotal = 100
                }
            ]
        });

        var task = Assert.Single(service.GetSnapshot());
        Assert.Contains("1/2 layers complete", task.StatusText);
        Assert.Equal(2, task.Details.Count);
        Assert.Equal("abc123def456", task.Details[0].Label);
        Assert.True(task.Details[0].IsComplete);
        Assert.Equal(50, task.Details[1].ProgressPercent);
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

    [Fact]
    public void TrackHostWorkerUpdateCreatesRunningTask()
    {
        using var service = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var host = new Host
        {
            Id = "host-1",
            Name = "linux-host"
        };

        var taskId = service.TrackHostWorkerUpdate(host, "v2.0.0", "3bf419d5a5a9f21f24f69dfb44b13639a4137448", "Queued update.");

        var task = Assert.Single(service.GetSnapshot());
        Assert.Equal(taskId, task.Id);
        Assert.Equal(LongRunningTaskKind.HostWorkerUpdate, task.Kind);
        Assert.Equal("host-1", task.HostId);
        Assert.Equal("v2.0.0+3bf419d5", task.Subject);
        Assert.Equal("Update HostWorker to v2.0.0+3bf419d5", task.Title);
        Assert.Equal(LongRunningTaskStatus.Running, task.Status);
        Assert.Equal(1, service.ActiveCount);
    }

    [Fact]
    public void HostWorkerUpdateStatusTransitionsToSucceededAndFailed()
    {
        using var service = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var host = new Host
        {
            Id = "host-1",
            Name = "linux-host",
            LatestAvailableVersion = "v2.0.0"
        };
        service.TrackHostWorkerUpdate(host, "v2.0.0", null, "Queued update.");

        service.UpdateHostWorkerUpdate(host, "Downloading", "Downloading update.", "v2.0.0", null);
        var running = Assert.Single(service.GetSnapshot());
        Assert.Equal(LongRunningTaskStatus.Running, running.Status);
        Assert.Equal("Downloading update.", running.StatusText);
        Assert.Equal(30, running.ProgressPercent);

        service.MarkHostWorkerUpdateSucceeded(host, "HostWorker is current.");
        var succeeded = Assert.Single(service.GetSnapshot());
        Assert.Equal(LongRunningTaskStatus.Succeeded, succeeded.Status);
        Assert.Equal(100, succeeded.ProgressPercent);
        Assert.Equal("HostWorker is current.", succeeded.StatusText);

        service.TrackHostWorkerUpdate(host, "v3.0.0", null, "Queued update.");
        service.MarkHostWorkerUpdateFailed(host, "Download failed.");
        var failed = Assert.Single(service.GetSnapshot());
        Assert.Equal(LongRunningTaskStatus.Failed, failed.Status);
        Assert.Equal("Download failed.", failed.Error);
        Assert.Equal(0, service.ActiveCount);
    }
}
