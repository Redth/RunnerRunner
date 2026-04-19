using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public class InitStepShellBuilderTests
{
    [Fact]
    public void BuildLinuxFragment_EmptySteps_ReturnsEmpty()
    {
        var result = InitStepShellBuilder.BuildLinuxFragment([], "PreRunner");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildLinuxFragment_WritesScriptAndInvokesSelectedShell()
    {
        var step = new ResolvedInitStep
        {
            Id = "abc",
            Name = "install sentry",
            Shell = InitStepShell.Bash,
            Script = "echo hello",
            TimeoutSeconds = 30,
        };

        var frag = InitStepShellBuilder.BuildLinuxFragment([step], "PreRunner");

        Assert.Contains("[init:install sentry]", frag);
        Assert.Contains("starting (phase=PreRunner", frag);
        Assert.Contains("cat > /tmp/rr-init-abc.script", frag);
        Assert.Contains("echo hello", frag);
        Assert.Contains("bash '/tmp/rr-init-abc.script'", frag);
        Assert.Contains("timeout 30", frag);
        Assert.Contains("exited rc=", frag);
    }

    [Fact]
    public void BuildLinuxFragment_UsesShForShShell()
    {
        var step = new ResolvedInitStep { Id = "x", Name = "n", Shell = InitStepShell.Sh, Script = "true" };
        var frag = InitStepShellBuilder.BuildLinuxFragment([step], "PreRunner");
        Assert.Contains("/bin/sh '/tmp/rr-init-x.script'", frag);
    }

    [Fact]
    public void BuildLinuxFragment_UsesPwshForPowerShellShell()
    {
        var step = new ResolvedInitStep { Id = "x", Name = "n", Shell = InitStepShell.PowerShell, Script = "Write-Host hi" };
        var frag = InitStepShellBuilder.BuildLinuxFragment([step], "PreRunner");
        Assert.Contains("pwsh -NoLogo -NoProfile -File '/tmp/rr-init-x.script'", frag);
    }

    [Fact]
    public void BuildLinuxFragment_ExitsOnFailure_WhenContinueOnErrorFalse()
    {
        var step = new ResolvedInitStep { Id = "x", Name = "fail", Shell = InitStepShell.Bash, Script = "false", ContinueOnError = false };
        var frag = InitStepShellBuilder.BuildLinuxFragment([step], "PreRunner");
        Assert.Contains("aborting (ContinueOnError=false)", frag);
        Assert.Contains("exit $rc", frag);
    }

    [Fact]
    public void BuildLinuxFragment_SkipsAbort_WhenContinueOnErrorTrue()
    {
        var step = new ResolvedInitStep { Id = "x", Name = "ok", Shell = InitStepShell.Bash, Script = "false", ContinueOnError = true };
        var frag = InitStepShellBuilder.BuildLinuxFragment([step], "PreRunner");
        Assert.DoesNotContain("aborting (ContinueOnError=false)", frag);
    }

    [Fact]
    public void BuildLinuxFragment_ExportsEnvVariables()
    {
        var step = new ResolvedInitStep
        {
            Id = "x",
            Name = "env-step",
            Shell = InitStepShell.Bash,
            Script = "true",
            EnvironmentVariables = new Dictionary<string, string> { ["FOO"] = "bar", ["HAS_QUOTE"] = "a'b" },
        };
        var frag = InitStepShellBuilder.BuildLinuxFragment([step], "PreRunner");
        Assert.Contains("export FOO='bar'", frag);
        // Single quote escaping uses the '"'"' trick
        Assert.Contains("export HAS_QUOTE='a'\"'\"'b'", frag);
    }

    [Fact]
    public void BuildLinuxFragment_UsesWorkingDirectory_WhenSet()
    {
        var step = new ResolvedInitStep
        {
            Id = "x",
            Name = "n",
            Shell = InitStepShell.Bash,
            Script = "true",
            WorkingDirectory = "/opt/work",
        };
        var frag = InitStepShellBuilder.BuildLinuxFragment([step], "PreRunner");
        Assert.Contains("cd '/opt/work' &&", frag);
    }

    [Fact]
    public void BuildWindowsFragment_EmitsPowerShellBlocks()
    {
        var step = new ResolvedInitStep
        {
            Id = "w1",
            Name = "step",
            Shell = InitStepShell.PowerShell,
            Script = "Write-Host hi",
            TimeoutSeconds = 45,
            EnvironmentVariables = new Dictionary<string, string> { ["K"] = "v" },
        };
        var frag = InitStepShellBuilder.BuildWindowsFragment([step], "PreRunner");
        Assert.Contains("Start-Job", frag);
        Assert.Contains("Wait-Job $rrJob -Timeout 45", frag);
        Assert.Contains("$env:K = 'v'", frag);
        Assert.Contains("[init:step]", frag);
    }
}
