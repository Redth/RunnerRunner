using RunnerRunner.Core.Hub;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.TestSupport;

internal sealed class RecordingHostCommandDispatcher : IHostCommandDispatcher
{
    private readonly object _gate = new();
    private readonly List<DispatchedHostCommand> _commands = [];

    public IReadOnlyList<DispatchedHostCommand> Commands
    {
        get
        {
            lock (_gate)
                return _commands.ToArray();
        }
    }

    public TCommand SingleCommand<TCommand>(HostCommandKind kind)
    {
        var matches = Commands.Where(command => command.Kind == kind).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected one {kind} command, found {matches.Length}.");

        return (TCommand)matches[0].Command;
    }

    public Task DispatchDeployRunnerAsync(string hostId, DeployRunnerCommand command)
        => RecordAsync(hostId, HostCommandKind.DeployRunner, command);

    public Task DispatchStopRunnerAsync(string hostId, StopRunnerCommand command)
        => RecordAsync(hostId, HostCommandKind.StopRunner, command);

    public Task DispatchCleanupOrphanAsync(string hostId, CleanupOrphanCommand command)
        => RecordAsync(hostId, HostCommandKind.CleanupOrphan, command);

    public Task DispatchListImagesAsync(string hostId, ListImagesCommand command)
        => RecordAsync(hostId, HostCommandKind.ListImages, command);

    public Task DispatchPullImageAsync(string hostId, PullImageCommand command)
        => RecordAsync(hostId, HostCommandKind.PullImage, command);

    public Task DispatchDeleteImageAsync(string hostId, DeleteImageCommand command)
        => RecordAsync(hostId, HostCommandKind.DeleteImage, command);

    public Task DispatchGetHostLogsAsync(string hostId, GetHostLogsCommand command)
        => RecordAsync(hostId, HostCommandKind.GetHostLogs, command);

    public Task DispatchGetRunnerLogsAsync(string hostId, GetRunnerLogsCommand command)
        => RecordAsync(hostId, HostCommandKind.GetRunnerLogs, command);

    public Task DispatchApplyHostWorkerUpdateAsync(string hostId, HostWorkerUpdateCommand command)
        => RecordAsync(hostId, HostCommandKind.ApplyHostWorkerUpdate, command);

    private Task RecordAsync(string hostId, HostCommandKind kind, object command)
    {
        lock (_gate)
            _commands.Add(new DispatchedHostCommand(hostId, kind, command));

        return Task.CompletedTask;
    }
}

internal sealed record DispatchedHostCommand(string HostId, HostCommandKind Kind, object Command);
