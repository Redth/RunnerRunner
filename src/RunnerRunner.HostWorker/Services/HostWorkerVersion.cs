using RunnerRunner.ServiceDefaults;

namespace RunnerRunner.HostWorker.Services;

internal static class HostWorkerVersion
{
    public static string Current
    {
        get
        {
            return RunnerRunnerBuildInfo.FromAssembly(typeof(HostWorkerVersion).Assembly).InformationalVersion;
        }
    }
}
