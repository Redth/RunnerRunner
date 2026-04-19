using RunnerRunner.Agent.Services;

namespace RunnerRunner.Agent.Tests.Services;

public class JobHookScriptBuilderTests
{
    [Fact]
    public void BuildBashScript_WrapsOutputInActionsGroup()
    {
        var script = JobHookScriptBuilder.BuildBashScript();

        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("::group::RunnerRunner environment", script);
        Assert.Contains("::endgroup::", script);
        Assert.Contains("RR_META_BACKEND", script);
        Assert.Contains("RR_META_IMAGE", script);
        Assert.Contains("RR_META_AGENT_VERSION", script);
    }

    [Fact]
    public void BuildPowerShellScript_WrapsOutputInActionsGroup()
    {
        var script = JobHookScriptBuilder.BuildPowerShellScript();

        Assert.Contains("::group::RunnerRunner environment", script);
        Assert.Contains("::endgroup::", script);
        Assert.Contains("$env:RR_META_BACKEND", script);
        Assert.Contains("$env:RR_META_IMAGE", script);
    }

    [Fact]
    public void WriteBashScript_CreatesExecutableFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rr-hook-test-{Guid.NewGuid():N}");
        try
        {
            var path = JobHookScriptBuilder.WriteBashScript(tempDir);

            Assert.True(File.Exists(path));
            Assert.Equal(JobHookScriptBuilder.BashFileName, Path.GetFileName(path));
            var content = File.ReadAllText(path);
            Assert.Contains("::group::RunnerRunner environment", content);

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute),
                    $"Expected bash script to be executable; got {mode}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void IsHookRequested_InterpretsSentinel(string value, bool expected)
    {
        var dict = new Dictionary<string, string> { [JobHookScriptBuilder.RequestedEnvVarName] = value };
        Assert.Equal(expected, JobHookScriptBuilder.IsHookRequested(dict));
    }

    [Fact]
    public void IsHookRequested_ReturnsFalse_WhenUnset()
    {
        Assert.False(JobHookScriptBuilder.IsHookRequested(new Dictionary<string, string>()));
    }
}
