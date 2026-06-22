using System.Text.RegularExpressions;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            ContainerImage = isContainer ? configuration["HostWorker:ContainerImage"] : null,
            NetworkAddresses = DetectNetworkAddresses()
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

    internal static List<string> DetectNetworkAddresses()
    {
        try
        {
            return NormalizeNetworkAddresses(
                NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Select(address => address.Address));
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (SocketException)
        {
            return [];
        }
    }

    internal static List<string> NormalizeNetworkAddresses(IEnumerable<IPAddress> addresses)
        => [.. addresses
            .Select(NormalizeNetworkAddress)
            .Where(address => address != null)
            .Select(address => address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(address => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(address => address, StringComparer.OrdinalIgnoreCase)];

    private static string? NormalizeNetworkAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.IsIPv6LinkLocal)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var value = address.ToString();
        var scopeIndex = value.IndexOf('%', StringComparison.Ordinal);
        return scopeIndex >= 0 ? value[..scopeIndex] : value;
    }

    private static string? NormalizeContainerId(string? value)
    {
        var candidate = value?.Trim();
        return !string.IsNullOrWhiteSpace(candidate) && ShortContainerIdPattern.IsMatch(candidate)
            ? candidate.ToLowerInvariant()
            : null;
    }
}
