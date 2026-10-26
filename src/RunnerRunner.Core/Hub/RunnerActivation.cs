using Orleans;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Hub;

[GenerateSerializer]
public sealed class ActivateRunnerCommand
{
    [Id(0)] public string InstanceId { get; set; } = "";
    [Id(1)] public string InstanceHandle { get; set; } = "";
    [Id(2)] public string JitConfig { get; set; } = "";
}

// This snapshot contains credential references only, never provider tokens or JIT blobs.
[GenerateSerializer]
public sealed class DeferredRunnerRegistration
{
    [Id(0)] public string CredentialId { get; set; } = "";
    [Id(1)] public List<string> Labels { get; set; } = [];
    [Id(2)] public string RunnerGroup { get; set; } = "Default";
    [Id(3)] public string? Repository { get; set; }
    [Id(4)] public string? GitHubInstallationId { get; set; }

    public static bool Required(RunnerProfile profile, HostPlatform platform) =>
        profile.Provider == RunnerProvider.GitHubActions &&
        profile.ExecutionBackend == ExecutionBackend.Docker && platform == HostPlatform.Windows &&
        profile.DockerConfig?.PrepareBeforeRegistration == true;
}
