using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

internal static class HostBackendLimitDefaults
{
    public static void ApplyToNewHost(Host host)
    {
        var capabilities = host.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);

        host.MaxDockerContainers = capabilities.Contains("docker") ? 10 : 0;
        host.MaxTartVMs = capabilities.Contains("tart") ? 3 : 0;
        host.MaxNativeProcesses = 5;
    }
}
