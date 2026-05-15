namespace RunnerRunner.HostWorker.Services;

internal sealed class HostWorkerPaths
{
    public HostWorkerPaths(IConfiguration configuration)
        : this(configuration, ResolveDefaultDataRoot, null)
    {
    }

    public HostWorkerPaths(IConfiguration configuration, ILogger<HostWorkerPaths> logger)
        : this(configuration, ResolveDefaultDataRoot, logger)
    {
    }

    internal HostWorkerPaths(IConfiguration configuration, Func<string> defaultDataRootFactory)
        : this(configuration, defaultDataRootFactory, null)
    {
    }

    private HostWorkerPaths(
        IConfiguration configuration,
        Func<string> defaultDataRootFactory,
        ILogger<HostWorkerPaths>? logger)
    {
        var dataRoot = configuration["HostWorker:DataRoot"];
        if (string.IsNullOrWhiteSpace(dataRoot))
            dataRoot = defaultDataRootFactory();

        DataRoot = dataRoot;

        var logRoot = configuration["HostWorker:LogRoot"];
        if (string.IsNullOrWhiteSpace(logRoot))
            logRoot = Path.Combine(DataRoot, "logs");

        LogRoot = logRoot;
        CommandJournalPath = Path.Combine(DataRoot, "journals", "commands.jsonl");

        logger?.LogInformation(
            "HostWorker paths resolved. DataRoot: {DataRoot}; LogRoot: {LogRoot}; CommandJournalPath: {CommandJournalPath}",
            DataRoot,
            LogRoot,
            CommandJournalPath);

        CreateRequiredDirectory(DataRoot, "HostWorker:DataRoot");
        CreateRequiredDirectory(LogRoot, "HostWorker:LogRoot");
        CreateRequiredDirectory(Path.GetDirectoryName(CommandJournalPath), nameof(CommandJournalPath));
    }

    public string DataRoot { get; }
    public string LogRoot { get; }
    public string CommandJournalPath { get; }

    private static void CreateRequiredDirectory(string? path, string pathName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"{pathName} resolved to an empty path.");

        Directory.CreateDirectory(path);
    }

    private static string ResolveDefaultDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RunnerRunner");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".runnerrunner");

        return "/var/lib/runnerrunner";
    }
}
