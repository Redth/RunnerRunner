namespace RunnerRunner.HostWorker.Tests.TestSupport;

internal sealed class HostWorkerTestDirectory : IDisposable
{
    private HostWorkerTestDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public static HostWorkerTestDirectory Create(string prefix = "hostworker")
        => new(System.IO.Path.Combine(
            Directory.GetCurrentDirectory(),
            "TestResults",
            $"{prefix}-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
