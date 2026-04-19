using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class InitStepResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsEmpty_WhenNoSteps()
    {
        var store = TestDocumentStore.Create();
        var profile = new RunnerProfile { Name = "p" };
        var result = await InitStepResolver.ResolveAsync(
            store, profile, new Dictionary<string, string>(), ExecutionBackend.Native, HostPlatform.Linux);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAsync_SkipsDisabledSteps()
    {
        var store = TestDocumentStore.Create();
        var profile = new RunnerProfile { Name = "p" };
        profile.InitSteps.Add(new RunnerInitStep { Name = "on", Enabled = true });
        profile.InitSteps.Add(new RunnerInitStep { Name = "off", Enabled = false });

        var result = await InitStepResolver.ResolveAsync(
            store, profile, new Dictionary<string, string>(), ExecutionBackend.Native, HostPlatform.Linux);

        Assert.Single(result);
        Assert.Equal("on", result[0].Name);
    }

    [Fact]
    public async Task ResolveAsync_ComposesEnv_BasePlusSetsPlusOverrides()
    {
        var store = TestDocumentStore.Create();
        var set = new EnvironmentVariableSet
        {
            Name = "tools",
            Priority = 10,
            Variables = new Dictionary<string, string> { ["FROM_SET"] = "1", ["BASE_VAR"] = "overridden-by-set" },
            SecretKeys = ["FROM_SET"],
        };
        await store.Insert(set);

        var profile = new RunnerProfile { Name = "p" };
        profile.InitSteps.Add(new RunnerInitStep
        {
            Name = "step1",
            EnvironmentVariableSetIds = [set.Id],
            EnvironmentOverrides = new Dictionary<string, string> { ["BASE_VAR"] = "final" },
            EnvironmentOverrideSecretKeys = ["STEP_SECRET"],
        });

        var baseEnv = new Dictionary<string, string> { ["BASE_VAR"] = "base", ["KEEP"] = "kept" };

        var result = await InitStepResolver.ResolveAsync(
            store, profile, baseEnv, ExecutionBackend.Native, HostPlatform.Linux);

        var step = Assert.Single(result);
        Assert.Equal("final", step.EnvironmentVariables["BASE_VAR"]);
        Assert.Equal("1", step.EnvironmentVariables["FROM_SET"]);
        Assert.Equal("kept", step.EnvironmentVariables["KEEP"]);
        Assert.Contains("FROM_SET", step.SecretKeys);
        Assert.Contains("STEP_SECRET", step.SecretKeys);
    }

    [Fact]
    public async Task ResolveAsync_ResolvesAutoShell_ByBackendAndPlatform()
    {
        var store = TestDocumentStore.Create();
        var profile = new RunnerProfile { Name = "p" };
        profile.InitSteps.Add(new RunnerInitStep { Name = "a", Shell = InitStepShell.Auto });

        var tart = await InitStepResolver.ResolveAsync(store, profile, new Dictionary<string, string>(), ExecutionBackend.Tart, HostPlatform.MacOS);
        Assert.Equal(InitStepShell.Bash, tart[0].Shell);

        var dockerWin = await InitStepResolver.ResolveAsync(store, profile, new Dictionary<string, string>(), ExecutionBackend.Docker, HostPlatform.Windows);
        Assert.Equal(InitStepShell.PowerShell, dockerWin[0].Shell);

        var dockerLinux = await InitStepResolver.ResolveAsync(store, profile, new Dictionary<string, string>(), ExecutionBackend.Docker, HostPlatform.Linux);
        Assert.Equal(InitStepShell.Bash, dockerLinux[0].Shell);
    }

    [Fact]
    public async Task ResolveAsync_PreservesExplicitShell()
    {
        var store = TestDocumentStore.Create();
        var profile = new RunnerProfile { Name = "p" };
        profile.InitSteps.Add(new RunnerInitStep { Name = "a", Shell = InitStepShell.Sh });

        var result = await InitStepResolver.ResolveAsync(store, profile, new Dictionary<string, string>(), ExecutionBackend.Docker, HostPlatform.Linux);
        Assert.Equal(InitStepShell.Sh, result[0].Shell);
    }

    [Fact]
    public async Task ResolveAsync_OrdersSetsByPriority()
    {
        var store = TestDocumentStore.Create();
        var low = new EnvironmentVariableSet { Name = "low", Priority = 1, Variables = new() { ["K"] = "low" } };
        var high = new EnvironmentVariableSet { Name = "high", Priority = 100, Variables = new() { ["K"] = "high" } };
        await store.Insert(low);
        await store.Insert(high);

        var profile = new RunnerProfile { Name = "p" };
        profile.InitSteps.Add(new RunnerInitStep
        {
            Name = "s",
            EnvironmentVariableSetIds = [high.Id, low.Id],
        });

        var result = await InitStepResolver.ResolveAsync(store, profile, new Dictionary<string, string>(), ExecutionBackend.Native, HostPlatform.Linux);
        // Priority ascending: low first, then high — so "high" should win.
        Assert.Equal("high", result[0].EnvironmentVariables["K"]);
    }
}
