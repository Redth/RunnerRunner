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

    [Theory]
    [InlineData("MAUI macOS Native-jit-be3d6782", "12345678-90ab-cdef-1234-567890abcdef")]
    [InlineData("macOS/arm64 (Xcode 16.4)", "")]
    [InlineData("../../..", "ABCDEF12")]
    [InlineData("profile-abcdef12", "abcdef12-3456-7890-abcd-ef1234567890")]
    public void CreateSafeRunnerDirectoryName_UsesShortHashToken(
        string runnerName,
        string instanceId)
    {
        var directoryName = NativeBackend.CreateSafeRunnerDirectoryName(runnerName, instanceId);

        Assert.Matches("^rr-[0-9a-f]{16}$", directoryName);
        Assert.DoesNotContain(' ', directoryName);
        Assert.DoesNotContain('/', directoryName);
        Assert.DoesNotContain('\\', directoryName);
        Assert.DoesNotContain('$', directoryName);
        Assert.DoesNotContain('\'', directoryName);
        Assert.DoesNotContain('"', directoryName);
        Assert.DoesNotContain("maui", directoryName);
    }

    [Fact]
    public void CreateSafeRunnerDirectoryName_IsStableAndIncludesInstanceId()
    {
        var first = NativeBackend.CreateSafeRunnerDirectoryName("same-runner", "instance-1");
        var second = NativeBackend.CreateSafeRunnerDirectoryName("same-runner", "instance-1");
        var differentInstance = NativeBackend.CreateSafeRunnerDirectoryName("same-runner", "instance-2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentInstance);
    }

    [Fact]
    public void GetDefaultRunnerBasePath_UsesShortWindowsRoot()
    {
        Assert.Equal(@"C:\rr", NativeBackend.GetDefaultRunnerBasePath(isWindows: true, homePath: @"C:\Users\runner"));
        Assert.Equal(
            Path.Combine("/Users/runner", ".runnerrunner"),
            NativeBackend.GetDefaultRunnerBasePath(isWindows: false, homePath: "/Users/runner"));
    }
}
