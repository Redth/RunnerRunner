using System.Runtime.InteropServices;
using RunnerRunner.Core.Models;

namespace RunnerRunner.HostSilo;

internal sealed record HostSiloIdentity(
    string HostId,
    string HostName,
    HostPlatform Platform,
    string Architecture);

internal static class HostSiloIdentityResolver
{
    public static HostSiloIdentity Resolve(IConfiguration config)
    {
        var platform = Enum.TryParse<HostPlatform>(config["HostSilo:Platform"], true, out var parsedPlatform)
            ? parsedPlatform
            : DetectCurrentPlatform();
        var architecture = config["HostSilo:Architecture"]
            ?? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        var advertisedIp = config["Orleans:AdvertisedIPAddress"];
        var defaultHostId = !string.IsNullOrWhiteSpace(advertisedIp)
            ? $"{platform.ToString().ToLowerInvariant()}-host-{advertisedIp}"
            : Environment.MachineName;
        var hostId = config["HostSilo:HostId"] ?? defaultHostId;
        var hostName = config["HostSilo:HostName"] ?? hostId;

        return new HostSiloIdentity(hostId, hostName, platform, architecture);
    }

    private static HostPlatform DetectCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return HostPlatform.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return HostPlatform.Windows;
        return HostPlatform.Linux;
    }
}
