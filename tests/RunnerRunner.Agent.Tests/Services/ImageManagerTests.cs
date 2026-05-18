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

    [Theory]
    [InlineData("Downloading: 45.2%", 45.2)]
    [InlineData("  100%", 100)]
    public void ParseTartProgress_ExtractsPercent(string input, double expected)
    {
        Assert.Equal(expected, ImageManager.ParseTartProgress(input));
    }

    [Fact]
    public void ParseTartProgress_ReturnsNullWhenNoPercentIsPresent()
    {
        Assert.Null(ImageManager.ParseTartProgress("pulling layer"));
    }
}
