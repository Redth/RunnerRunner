using RunnerRunner.Agent.Backends;

namespace RunnerRunner.Agent.Tests.Backends;

public class TartBackendTests
{
    [Fact]
    public void ParseListOutput_ParsesRunningAndStoppedVms()
    {
        const string output = """
        [
          { "Name": "rr-managed", "State": "running" },
          { "Name": "external-build-vm", "State": "running" },
          { "Name": "stopped-vm", "State": "stopped" }
        ]
        """;

        var vms = TartBackend.ParseListOutput(output);

        Assert.Equal(3, vms.Count);
        Assert.Equal(2, vms.Count(vm => vm.IsRunning));
        Assert.Contains(vms, vm => vm.Name == "external-build-vm" && vm.IsRunning);
    }

    [Fact]
    public void ParseListOutput_ReturnsEmptyListForWhitespace()
    {
        Assert.Empty(TartBackend.ParseListOutput("   "));
    }

    [Fact]
    public void ParseListOutput_ReadsCaseInsensitivePropertyNames()
    {
        const string output = """
        [
          { "name": "rr-lower", "state": "running" },
          { "NAME": "rr-upper", "STATE": "stopped" }
        ]
        """;

        var vms = TartBackend.ParseListOutput(output);

        Assert.Equal(2, vms.Count);
        Assert.Contains(vms, vm => vm.Name == "rr-lower" && vm.IsRunning);
        Assert.Contains(vms, vm => vm.Name == "rr-upper" && !vm.IsRunning);
    }
    [Fact]
    public void PasswordIsOnlyInChildEnvironmentAndArgumentsAreNotShellInterpolated()
    {
        const string password = "test-only-'$(echo nope)\" secret";
        var start = TartBackend.BuildSshpassStartInfo("/path with spaces/sshpass", "runner", "192.0.2.5", password);
        Assert.Equal(password, start.Environment["SSHPASS"]);
        Assert.DoesNotContain(password, start.Arguments);
        Assert.DoesNotContain(password, string.Join(" ", start.ArgumentList));
        Assert.Equal("-e", start.ArgumentList[0]);
        Assert.Equal("ssh", start.ArgumentList[1]);
        Assert.DoesNotContain("-p", start.ArgumentList);
        Assert.True(start.RedirectStandardInput);
        Assert.False(start.UseShellExecute);
    }
}
