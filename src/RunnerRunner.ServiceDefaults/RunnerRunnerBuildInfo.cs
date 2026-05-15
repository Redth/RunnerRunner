using System.Reflection;

namespace RunnerRunner.ServiceDefaults;

public sealed record RunnerRunnerBuildInfo(
    string ApplicationName,
    string AssemblyVersion,
    string InformationalVersion,
    string CommitSha,
    string BuildTag,
    string Configuration)
{
    public static RunnerRunnerBuildInfo FromEntryAssembly()
    {
        return FromAssembly(Assembly.GetEntryAssembly());
    }

    public static RunnerRunnerBuildInfo FromAssembly(Assembly? assembly)
    {
        assembly ??= typeof(RunnerRunnerBuildInfo).Assembly;

        var assemblyName = assembly.GetName();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
            informationalVersion = assemblyName.Version?.ToString() ?? "0.0.0";

        return new RunnerRunnerBuildInfo(
            ApplicationName: assemblyName.Name ?? "RunnerRunner",
            AssemblyVersion: assemblyName.Version?.ToString() ?? "0.0.0",
            InformationalVersion: informationalVersion,
            CommitSha: GetCommitSha(assembly, informationalVersion),
            BuildTag: GetMetadataValue(assembly, "RunnerRunnerBuildTag"),
            Configuration: assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown");
    }

    private static string GetCommitSha(Assembly assembly, string informationalVersion)
    {
        var commitSha = GetMetadataValue(assembly, "RunnerRunnerCommitSha");
        if (!IsUnknown(commitSha))
            return commitSha;

        commitSha = GetMetadataValue(assembly, "SourceRevisionId");
        if (!IsUnknown(commitSha))
            return commitSha;

        var metadataSeparatorIndex = informationalVersion.LastIndexOf('+');
        if (metadataSeparatorIndex >= 0 && metadataSeparatorIndex + 1 < informationalVersion.Length)
            return informationalVersion[(metadataSeparatorIndex + 1)..];

        return "unknown";
    }

    private static string GetMetadataValue(Assembly assembly, string key)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static bool IsUnknown(string value)
    {
        return string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);
    }
}
