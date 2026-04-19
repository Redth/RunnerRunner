using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public class InitStepExecutorTests
{
    private static string WorkDir => Path.GetTempPath();

    [Fact]
    public async Task RunAsync_SkipsStepsNotMatchingPhase()
    {
        var exec = new InitStepExecutor(NullLogger.Instance);
        var step = new ResolvedInitStep
        {
            Id = "x",
            Name = "post-only",
            Phase = InitStepPhase.PostExit,
            Shell = InitStepShell.Bash,
            Script = "exit 1", // would fail
            TimeoutSeconds = 10,
        };

        await exec.RunAsync([step], InitStepPhase.PreRunner, WorkDir, new Dictionary<string, string>(), null, CancellationToken.None);
    }

    [Fact(Skip = "Requires bash; run locally on macOS/Linux")]
    public async Task RunAsync_HappyPath_Succeeds()
    {
        if (OperatingSystem.IsWindows()) return;

        var exec = new InitStepExecutor(NullLogger.Instance);
        var marker = Path.Combine(Path.GetTempPath(), $"rr-init-test-{Guid.NewGuid():N}");
        try
        {
            var step = new ResolvedInitStep
            {
                Id = "ok",
                Name = "touch-marker",
                Phase = InitStepPhase.PreRunner,
                Shell = InitStepShell.Bash,
                Script = $"touch '{marker}'",
                TimeoutSeconds = 10,
            };

            await exec.RunAsync([step], InitStepPhase.PreRunner, WorkDir, new Dictionary<string, string>(), null, CancellationToken.None);
            Assert.True(File.Exists(marker), "Init step should have created the marker file");
        }
        finally
        {
            try { File.Delete(marker); } catch { }
        }
    }

    [Fact(Skip = "Requires bash; run locally on macOS/Linux")]
    public async Task RunAsync_FailureAborts_WhenContinueOnErrorFalse()
    {
        if (OperatingSystem.IsWindows()) return;

        var exec = new InitStepExecutor(NullLogger.Instance);
        var step = new ResolvedInitStep
        {
            Id = "fail",
            Name = "bad-step",
            Phase = InitStepPhase.PreRunner,
            Shell = InitStepShell.Bash,
            Script = "exit 7",
            TimeoutSeconds = 10,
            ContinueOnError = false,
        };

        var ex = await Assert.ThrowsAsync<InitStepFailedException>(() =>
            exec.RunAsync([step], InitStepPhase.PreRunner, WorkDir, new Dictionary<string, string>(), null, CancellationToken.None));
        Assert.Equal("bad-step", ex.StepName);
        Assert.Equal(7, ex.ExitCode);
    }

    [Fact(Skip = "Requires bash; run locally on macOS/Linux")]
    public async Task RunAsync_FailureIgnored_WhenContinueOnErrorTrue()
    {
        if (OperatingSystem.IsWindows()) return;

        var exec = new InitStepExecutor(NullLogger.Instance);
        var step = new ResolvedInitStep
        {
            Id = "fail",
            Name = "bad-step",
            Phase = InitStepPhase.PreRunner,
            Shell = InitStepShell.Bash,
            Script = "exit 7",
            TimeoutSeconds = 10,
            ContinueOnError = true,
        };

        await exec.RunAsync([step], InitStepPhase.PreRunner, WorkDir, new Dictionary<string, string>(), null, CancellationToken.None);
    }

    [Fact(Skip = "Requires bash; run locally on macOS/Linux")]
    public async Task RunAsync_TimeoutKillsStep()
    {
        if (OperatingSystem.IsWindows()) return;

        var exec = new InitStepExecutor(NullLogger.Instance);
        var step = new ResolvedInitStep
        {
            Id = "slow",
            Name = "slow-step",
            Phase = InitStepPhase.PreRunner,
            Shell = InitStepShell.Bash,
            Script = "sleep 30",
            TimeoutSeconds = 1,
            ContinueOnError = false,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InitStepFailedException>(() =>
            exec.RunAsync([step], InitStepPhase.PreRunner, WorkDir, new Dictionary<string, string>(), null, CancellationToken.None));
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"Should time out quickly, took {sw.Elapsed.TotalSeconds}s");
        Assert.Contains("timed out", ex.Message);
    }
}
