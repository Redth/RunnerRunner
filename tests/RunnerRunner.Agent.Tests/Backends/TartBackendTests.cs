using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;

namespace RunnerRunner.Agent.Tests.Backends;

public class TartBackendTests
{
    [Theory]
    [InlineData("ghcr.io/example/runner:latest", "ghcr.io")]
    [InlineData("localhost:5000/example/runner:latest", "localhost:5000")]
    public void PrivateCloneScopesCredentialsToChildEnvironment(string image, string host)
    {
        var start = TartBackend.BuildPrivateCloneStartInfo(image, "rr-fixture", "fixture-user", "fixture-secret");
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "clone", image, "rr-fixture" }, start.ArgumentList);
        Assert.Equal(host, start.Environment["TART_REGISTRY_HOSTNAME"]);
        Assert.Equal("fixture-user", start.Environment["TART_REGISTRY_USERNAME"]);
        Assert.Equal("fixture-secret", start.Environment["TART_REGISTRY_PASSWORD"]);
        Assert.DoesNotContain("fixture-secret", start.Arguments);
        Assert.DoesNotContain("fixture-secret", string.Join(" ", start.ArgumentList));
    }

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
    public void ParseListOutput_RejectsAmbiguousEmptyInventory()
    {
        Assert.Throws<InvalidDataException>(() => TartBackend.ParseListOutput("   "));
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
    [Theory]
    [InlineData("stop")]
    [InlineData("delete")]
    [InlineData("still-present")]
    [InlineData("inventory")]
    public async Task CleanupFailureCannotConfirmRelease(string failure)
    {
        var commands = new List<string>();
        var lists = 0;
        var backend = new TartBackend(NullLogger<TartBackend>.Instance, (_, args, _) =>
        {
            commands.Add(args);
            if (args == "list --format json")
            {
                lists++;
                if (failure == "inventory") return Task.FromResult((1, "unavailable"));
                return Task.FromResult((0, lists == 1 ? "[{\"Name\":\"rr-owned\",\"State\":\"running\"}]" : "[{\"Name\":\"rr-owned\",\"State\":\"stopped\"}]"));
            }
            return Task.FromResult((args.StartsWith(failure + " ") ? 1 : 0, ""));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StopRunnerAsync("rr-owned"));
        if (failure == "stop" || failure == "inventory") Assert.DoesNotContain("delete rr-owned", commands);
    }

    [Fact]
    public async Task CleanupIsIdempotentOnlyAfterParsedAbsence()
    {
        var commands = new List<string>();
        var backend = new TartBackend(NullLogger<TartBackend>.Instance, (_, args, _) =>
        {
            commands.Add(args);
            return Task.FromResult((0, "[]"));
        });
        await backend.StopRunnerAsync("rr-absent");
        Assert.Equal(["list --format json"], commands);
    }

}
