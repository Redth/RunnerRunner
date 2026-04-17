using RunnerRunner.Agent.Backends;

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
}
