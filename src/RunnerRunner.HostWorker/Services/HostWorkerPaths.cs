namespace RunnerRunner.HostWorker.Services;

internal sealed class HostWorkerPaths
{
    public HostWorkerPaths(IConfiguration configuration)
    {
        var dataRoot = configuration["HostWorker:DataRoot"];
        if (string.IsNullOrWhiteSpace(dataRoot))
            dataRoot = ResolveDefaultDataRoot();

        DataRoot = dataRoot;
        LogRoot = configuration["HostWorker:LogRoot"] ?? Path.Combine(DataRoot, "logs");
        CommandJournalPath = Path.Combine(DataRoot, "journals", "commands.jsonl");

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(CommandJournalPath)!);
    }

    public string DataRoot { get; }
    public string LogRoot { get; }
    public string CommandJournalPath { get; }

    private static string ResolveDefaultDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RunnerRunner");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".runnerrunner");

        return "/var/lib/runnerrunner";
    }
}
