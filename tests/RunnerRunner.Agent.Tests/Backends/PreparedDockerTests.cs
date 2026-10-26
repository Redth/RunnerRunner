using System.Text;
using System.Text.Json;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public class PreparedDockerTests
{
    [Fact]
    public void ReadinessCannotAcceptCredentialsOrOptionalChecks()
    {
        var request = new RunnerStartRequest { RunnerName = "fixture", Provider = RunnerProvider.GitHubActions };
        Assert.Throws<InvalidOperationException>(() => DockerBackend.ValidatePreparedRequest(request, "windows"));
        request.InitSteps.Add(new ResolvedInitStep
        { Name = "required health", Script = "exit 0", Phase = InitStepPhase.PreRunner, TimeoutSeconds = 10 });
        DockerBackend.ValidatePreparedRequest(request, "windows");
        request.JitConfig = "must-not-exist-before-readiness";
        Assert.Throws<InvalidOperationException>(() => DockerBackend.ValidatePreparedRequest(request, "windows"));
        request.JitConfig = null;
        request.InitSteps[0].ContinueOnError = true;
        Assert.Throws<InvalidOperationException>(() => DockerBackend.ValidatePreparedRequest(request, "windows"));
        request.InitSteps[0].ContinueOnError = false;
        Assert.Throws<InvalidOperationException>(() => DockerBackend.ValidatePreparedRequest(request, "linux"));
    }

    [Fact]
    public void PrivateInputBindsCredentialToNativeClaim_AndExcludesItFromExecArguments()
    {
        var owner = new NativeRunnerOwnership { HostId = "host", InstanceId = "instance", ProfileId = "profile",
            RunnerName = "runner", IntentId = Guid.NewGuid().ToString("N") };
        var input = Encoding.ASCII.GetString(DockerBackend.BuildActivationInput(owner, "private-fixture-jit"));
        Assert.Single(input.TrimEnd('\n').Split('\n'));
        using var json = JsonDocument.Parse(Convert.FromBase64String(input));
        Assert.Equal(owner.InstanceId, json.RootElement.GetProperty("instanceId").GetString());
        Assert.Equal(owner.IntentId, json.RootElement.GetProperty("intentId").GetString());
        Assert.Equal("private-fixture-jit", json.RootElement.GetProperty("jitConfig").GetString());
        var writer = DockerBackend.BuildActivationWriteScript(owner.IntentId);
        Assert.DoesNotContain("private-fixture-jit", writer);
        Assert.Contains("[IO.FileMode]::CreateNew", writer);
        Assert.Contains("[Console]::In.ReadLine()", writer);
        var argv = DockerBackend.BuildPreparedWindowsEntrypoint(owner.IntentId, []);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(argv[^1]));
        Assert.DoesNotContain("private-fixture-jit", script);
        Assert.Contains("AddMinutes(15)", script);
        Assert.Contains("Remove-Item -LiteralPath $activation", script);
        Assert.True(script.IndexOf("Remove-Item -LiteralPath $activation", StringComparison.Ordinal) <
                    script.IndexOf("& $run --jitconfig", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => DockerBackend.BuildPreparedWindowsEntrypoint("../foreign", []));
    }
}
