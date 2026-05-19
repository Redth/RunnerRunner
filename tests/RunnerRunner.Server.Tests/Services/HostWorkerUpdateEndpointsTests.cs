using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerUpdateEndpointsTests
{
    [Theory]
    [InlineData(null, "abc", "abc", true)]
    [InlineData(false, "abc", "abc", true)]
    [InlineData(false, "abc", "bad", false)]
    [InlineData(false, "abc", null, false)]
    [InlineData(true, "abc", null, true)]
    public void IsArtifactDownloadAuthorized_AcceptsValidTokenOrMatchingChecksum(
        bool? enrollmentTokenAuthorized,
        string? assetSha256,
        string? providedSha256,
        bool expected)
    {
        var authorized = HostWorkerUpdateEndpoints.IsArtifactDownloadAuthorized(
            enrollmentTokenAuthorized,
            assetSha256,
            providedSha256);

        Assert.Equal(expected, authorized);
    }
}
