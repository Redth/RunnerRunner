using RunnerRunner.Agent.Services;

namespace RunnerRunner.Agent.Tests.Services;

public class ImageManagerTests
{
    [Theory]
    [InlineData("1GB", 1_073_741_824L)]
    [InlineData("500kB", 512_000L)]
    [InlineData("12B", 12L)]
    [InlineData("not-a-size", 0L)]
    public void ParseDockerSize_ParsesDockerCliUnits(string input, long expected)
    {
        Assert.Equal(expected, ImageManager.ParseDockerSize(input));
    }

    [Fact]
    public void ParseDockerPullProgress_ExtractsPercentAndByteCounts()
    {
        var progress = ImageManager.ParseDockerPullProgress("Downloading 50MB/100MB");

        Assert.NotNull(progress);
        Assert.Equal(50, progress.Value.percent);
        Assert.Equal(52_428_800L, progress.Value.downloaded);
        Assert.Equal(104_857_600L, progress.Value.total);
    }

    [Fact]
    public void ImagePullProgressTracker_AggregatesDockerLayerProgress()
    {
        var tracker = new ImageManager.ImagePullProgressTracker("library/ubuntu:latest");

        tracker.Update("abc123def456: Pulling fs layer");
        tracker.Update("def456abc123: Pulling fs layer");
        var snapshot = tracker.Update("abc123def456: Downloading 50MB/100MB");

        Assert.Equal(2, snapshot.Layers.Count);
        Assert.Equal(20, snapshot.ProgressPercent);
        Assert.Equal(52_428_800L, snapshot.BytesDownloaded);
        Assert.Equal(104_857_600L, snapshot.BytesTotal);

        snapshot = tracker.Update("abc123def456: Pull complete");
        Assert.Equal(50, snapshot.ProgressPercent);

        snapshot = tracker.Update("def456abc123: Already exists");
        Assert.Equal(100, snapshot.ProgressPercent);
        Assert.All(snapshot.Layers, layer => Assert.True(layer.IsComplete));
    }

    [Theory]
    [InlineData("Downloading: 45.2%", 45.2)]
    [InlineData("  100%", 100)]
    public void ParseTartProgress_ExtractsPercent(string input, double expected)
    {
        Assert.Equal(expected, ImageManager.ParseTartProgress(input));
    }

    [Fact]
    public void ImagePullProgressTracker_TracksTartPercentAsSingleLayer()
    {
        var tracker = new ImageManager.ImagePullProgressTracker("ghcr.io/example/macos:sonoma");

        var snapshot = tracker.Update("Downloading: 45.2%");

        var layer = Assert.Single(snapshot.Layers);
        Assert.Equal("ghcr.io/example/macos:sonoma", layer.Id);
        Assert.Equal(45.2, snapshot.ProgressPercent);
        Assert.Equal("Downloading: 45.2%", layer.Status);
    }

    [Fact]
    public void ParseTartProgress_ReturnsNullWhenNoPercentIsPresent()
    {
        Assert.Null(ImageManager.ParseTartProgress("pulling layer"));
    }
}
