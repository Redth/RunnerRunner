using System.Diagnostics;
using RunnerRunner.Agent.Backends;

namespace RunnerRunner.Agent.Tests.Backends;

public class TartSshProcessTests
{
    [Fact]
    public async Task DrainsBothPipesWhileSendingLargeInput()
    {
        if (OperatingSystem.IsWindows()) return; // Tart hosts and these SSH fixtures use Unix.
        var start = Shell("head -c 262144 /dev/zero >&2; cat >/dev/null; head -c 262144 /dev/zero");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await TartBackend.RunSshProcessAsync(start, new string('x', 2 * 1024 * 1024), deadline.Token);
    }

    [Fact]
    public async Task NonzeroExitDoesNotExposeRemoteOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        var start = Shell("cat >/dev/null; printf 'private-fixture-output' >&2; exit 37");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TartBackend.RunSshProcessAsync(start, "", CancellationToken.None));
        Assert.Contains("37", error.Message);
        Assert.DoesNotContain("private-fixture-output", error.Message);
    }

    [Fact]
    public async Task CancellationStopsTheOwnedProcess()
    {
        if (OperatingSystem.IsWindows()) return;
        var pidFile = Path.Combine(Path.GetTempPath(), "rr-ssh-process-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = Shell("echo $$ > \"$1\"; exec sleep 60");
        start.ArgumentList.Add("fixture");
        start.ArgumentList.Add(pidFile);
        Task? running = null;
        try
        {
            running = TartBackend.RunSshProcessAsync(start, "", cancel.Token);
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
                await Task.Delay(20, deadline.Token);
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile, deadline.Token));
            using var process = Process.GetProcessById(pid);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            cancel.Cancel();
            if (running is not null) { try { await running; } catch { } }
            File.Delete(pidFile);
        }
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo("/bin/sh")
        { UseShellExecute = false, RedirectStandardInput = true,
          RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        return start;
    }
}
