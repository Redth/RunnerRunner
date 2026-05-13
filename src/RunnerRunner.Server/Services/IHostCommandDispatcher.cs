using RunnerRunner.Core.Hub;

namespace RunnerRunner.Server.Services;

public interface IHostCommandDispatcher
{
    Task DispatchDeployRunnerAsync(string hostId, DeployRunnerCommand command);
    Task DispatchStopRunnerAsync(string hostId, StopRunnerCommand command);
    Task DispatchCleanupOrphanAsync(string hostId, CleanupOrphanCommand command);
    Task DispatchListImagesAsync(string hostId, ListImagesCommand command);
    Task DispatchPullImageAsync(string hostId, PullImageCommand command);
    Task DispatchDeleteImageAsync(string hostId, DeleteImageCommand command);
    Task DispatchGetHostLogsAsync(string hostId, GetHostLogsCommand command);
    Task DispatchGetRunnerLogsAsync(string hostId, GetRunnerLogsCommand command);
    Task DispatchApplyHostWorkerUpdateAsync(string hostId, HostWorkerUpdateCommand command);
}
