using System.Reflection;

namespace RunnerRunner.HostWorker.Services;

internal static class HostWorkerVersion
{
    public static string Current
    {
        get
        {
            var assembly = typeof(HostWorkerVersion).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational;

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}
