using RunnerRunner.Agent.Backends;
using Microsoft.Extensions.Logging.Abstractions;

namespace RunnerRunner.Agent.Tests.Backends;

public class NativeBackendTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("latest", true)]
    [InlineData("LATEST", true)]
    [InlineData("2.333.1", false)]
    public void NeedsLatestResolution_BehavesAsExpected(string? version, bool expected)
    {
        Assert.Equal(expected, NativeBackend.NeedsLatestResolution(version));
    }

    [Theory]
    [InlineData("v2.333.1", "2.333.1")]
    [InlineData("V2.333.1", "2.333.1")]
    [InlineData("2.333.1", "2.333.1")]
    [InlineData(null, null)]
    public void NormalizeVersionTag_StripsLeadingV(string? input, string? expected)
    {
        Assert.Equal(expected, NativeBackend.NormalizeVersionTag(input));
    }

    [Fact]
    public async Task UnknownInstance_ReportsNotFoundHealthAndStopDoesNotThrow()
    {
        var backend = new NativeBackend(NullLogger<NativeBackend>.Instance);

        var health = await backend.GetHealthAsync("missing-process");
        await backend.StopRunnerAsync("missing-process");

        Assert.False(health.IsRunning);
        Assert.Equal("not_found", health.Status);
    }

    [Fact]
    public void ExpandTokens_ReplacesKnownTokensAndLeavesUnknownTokens()
    {
        var expanded = NativeBackend.ExpandTokens(
            "${BASE_PATH}/instances/${RUNNER_NAME}/${UNKNOWN}",
            new Dictionary<string, string>
            {
                ["BASE_PATH"] = "/runner-root",
                ["RUNNER_NAME"] = "runner-1"
            });

        Assert.Equal("/runner-root/instances/runner-1/${UNKNOWN}", expanded);
    }
}
