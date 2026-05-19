using RunnerRunner.ServiceDefaults;

namespace RunnerRunner.HostWorker.Services;

internal static class HostWorkerVersion
{
    public static RunnerRunnerBuildInfo BuildInfo
        => RunnerRunnerBuildInfo.FromAssembly(typeof(HostWorkerVersion).Assembly);

    public static string Current
        => BuildInfo.InformationalVersion;

    public static string CommitSha
        => BuildInfo.CommitSha;

    public static string BuildTag
        => BuildInfo.BuildTag;
}
