using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerSelfUpdaterTests
{
    [Theory]
    [InlineData("https://ci.example.test/api/hostworker-updates/github-artifacts/1/assets/worker.tar.gz?sha256=abc", true)]
    [InlineData("https://ci.example.test/prefix/api/hostworker-updates/artifacts/local/worker.tar.gz?sha256=abc", true)]
    [InlineData("https://github.com/redth/RunnerRunner/releases/download/v1/worker.tar.gz", false)]
    [InlineData("not a url", false)]
    public void IsRunnerRunnerUpdateAssetUrl_MatchesOnlyHostedUpdateEndpoints(string url, bool expected)
        => Assert.Equal(expected, HostWorkerSelfUpdater.IsRunnerRunnerUpdateAssetUrl(url));
}
