using System.Text.RegularExpressions;
using RunnerRunner.Core.Hub;

namespace RunnerRunner.HostWorker.Services;

internal static class HostWorkerRuntimeDetector
{
    private static readonly Regex FullContainerIdPattern = new("[0-9a-f]{64}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShortContainerIdPattern = new("^[0-9a-f]{12,64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static HostWorkerRuntimeInfo Detect(IConfiguration configuration)
    {
        var isContainer = IsContainer();
        return new HostWorkerRuntimeInfo
        {
            IsContainer = isContainer,
            ContainerId = isContainer ? TryDetectContainerId() : null,
            ContainerImage = isContainer ? configuration["HostWorker:ContainerImage"] : null
        };
    }

    internal static bool IsContainer()
        => IsContainer(Environment.GetEnvironmentVariable, File.Exists);

    internal static bool IsContainer(Func<string, string?> getEnvironmentVariable, Func<string, bool> fileExists)
        => string.Equals(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
           || fileExists("/.dockerenv");

    internal static string? TryDetectContainerId()
        => TryDetectContainerId(Environment.GetEnvironmentVariable, () =>
        {
            try
            {
                return File.Exists("/proc/self/cgroup") ? File.ReadAllText("/proc/self/cgroup") : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }, Environment.MachineName);

    internal static string? TryDetectContainerId(
        Func<string, string?> getEnvironmentVariable,
        Func<string?> readCgroup,
        string machineName)
    {
        foreach (var candidate in new[]
                 {
                     getEnvironmentVariable("HOSTNAME"),
                     getEnvironmentVariable("COMPUTERNAME"),
                     machineName
                 })
        {
            var normalized = NormalizeContainerId(candidate);
            if (normalized != null)
                return normalized;
        }

        var cgroup = readCgroup();
        if (string.IsNullOrWhiteSpace(cgroup))
            return null;

        var match = FullContainerIdPattern.Match(cgroup);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static string? NormalizeContainerId(string? value)
    {
        var candidate = value?.Trim();
        return !string.IsNullOrWhiteSpace(candidate) && ShortContainerIdPattern.IsMatch(candidate)
            ? candidate.ToLowerInvariant()
            : null;
    }
}
