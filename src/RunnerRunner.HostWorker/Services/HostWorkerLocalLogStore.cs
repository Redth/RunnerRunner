using System.Text;
using RunnerRunner.Core.HostWorkers;

namespace RunnerRunner.HostWorker.Services;

internal sealed class HostWorkerLocalLogStore
{
    private readonly HostWorkerPaths _paths;

    public HostWorkerLocalLogStore(HostWorkerPaths paths)
    {
        _paths = paths;
    }

    public async Task<HostWorkerLogFrame> AppendAsync(
        string streamKind,
        string streamId,
        string text,
        string? runnerInstanceId,
        CancellationToken cancellationToken)
    {
        var filePath = ResolveStreamPath(streamKind, streamId);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var bytes = Encoding.UTF8.GetBytes(text.EndsWith('\n') ? text : text + Environment.NewLine);
        await using var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var offset = stream.Length;
        stream.Seek(offset, SeekOrigin.Begin);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        return new HostWorkerLogFrame
        {
            StreamKind = streamKind,
            StreamId = streamId,
            RunnerInstanceId = runnerInstanceId,
            Offset = offset,
            Text = Encoding.UTF8.GetString(bytes),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private string ResolveStreamPath(string streamKind, string streamId)
    {
        var safeKind = Sanitize(streamKind);
        var safeId = Sanitize(streamId);
        return Path.Combine(_paths.LogRoot, "streams", safeKind, safeId + ".log");
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
                builder.Append(ch);
            else
                builder.Append('_');
        }

        return builder.Length == 0 ? "default" : builder.ToString();
    }
}
