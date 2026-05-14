using System.Runtime.InteropServices;
using RunnerRunner.Core.Models;

namespace RunnerRunner.HostWorker;

internal sealed record HostWorkerIdentity(
    string HostId,
    string HostName,
    HostPlatform Platform,
    string Architecture);

internal static class HostWorkerIdentityResolver
{
    public static HostWorkerIdentity Resolve(IConfiguration config)
    {
        var platform = Enum.TryParse<HostPlatform>(config["HostWorker:Platform"], true, out var parsedPlatform)
            ? parsedPlatform
            : DetectCurrentPlatform();
        var architecture = config["HostWorker:Architecture"];
        if (string.IsNullOrWhiteSpace(architecture))
            architecture = RuntimeInformation.OSArchitecture.ToString();

        var defaultHostId = Environment.MachineName;
        var hostId = config["HostWorker:HostId"];
        if (string.IsNullOrWhiteSpace(hostId))
            hostId = defaultHostId;

        var hostName = config["HostWorker:HostName"];
        if (string.IsNullOrWhiteSpace(hostName))
            hostName = hostId;

        return new HostWorkerIdentity(hostId, hostName, platform, architecture);
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
