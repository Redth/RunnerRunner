namespace RunnerRunner.HostWorker.Services;

/// <summary>One process may mutate native ownership in this worker's data root.</summary>
internal sealed class HostWorkerProcessLock : IDisposable
{
    private readonly FileStream _stream;

    public HostWorkerProcessLock(HostWorkerPaths paths)
    {
        // Keep the inode/path after close. Deleting it permits two processes to
        // lock different files at the same path while an older process still lives.
        _stream = new FileStream(Path.Combine(paths.DataRoot, "worker.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => _stream.Dispose();
}
