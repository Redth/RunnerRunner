using RunnerRunner.Core.HostWorkers;

namespace RunnerRunner.HostWorker.Services;

internal interface IHostWorkerEventSink
{
    ValueTask PublishAsync(HostWorkerMessage message, CancellationToken cancellationToken = default);
}
