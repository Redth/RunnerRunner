using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;
using RunnerRunner.Core.Models;
using RunnerRunner.Core.Hub;
using RunnerRunner.Server.Grains.Events;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Grains;

// TODO: Add host-affinity grain placement so this grain activates on the silo
// whose "hostId" metadata matches the runner's assigned host. This enables
// local backend execution (Docker/Tart/Native) without SignalR round-trips.
// See HostGrain.cs for placement strategy options.
public class RunnerInstanceGrain : Grain, IRunnerInstanceGrain
{
    private readonly IPersistentState<RunnerInstanceGrainState> _state;
    private readonly ILogger<RunnerInstanceGrain> _logger;
    private readonly IServiceProvider _serviceProvider;

    private IGrainTimer? _pendingTimer;
    private IGrainTimer? _registrationTimer;
    private IGrainTimer? _dynamicTimer;
    private IGrainTimer? _stopTimer;
    private IGrainTimer? _healthTimer;

    public RunnerInstanceGrain(
        [PersistentState("runner", "PersistentStore")]
        IPersistentState<RunnerInstanceGrainState> state,
        ILogger<RunnerInstanceGrain> logger,
        IServiceProvider serviceProvider)
    {
        _state = state;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public Task<RunnerInstanceGrainState> GetState() => Task.FromResult(_state.State);

    public async Task Initialize(string hostId, string profileId, string runnerName, string provisioningMode, string? jobId = null, string? webhookEventId = null, string? provisioningRuleId = null)
    {
        _state.State.HostId = hostId;
        _state.State.ProfileId = profileId;
        _state.State.RunnerName = runnerName;
        _state.State.ProvisioningMode = provisioningMode;
        _state.State.JobId = jobId;
        _state.State.WebhookEventId = webhookEventId;
        _state.State.ProvisioningRuleId = provisioningRuleId;
        _state.State.Status = RunnerInstanceStatus.Pending;
        _state.State.CreatedAt = DateTime.UtcNow;

        RecordStatusTransition(RunnerInstanceStatus.Pending, "Instance created", "grain_call");

        // Look up the profile to get the backend
        var profileGrain = GrainFactory.GetGrain<IProfileGrain>(profileId);
        var profile = await profileGrain.GetProfile();
        if (profile != null)
            _state.State.Backend = profile.ExecutionBackend;

        await _state.WriteStateAsync();

        _logger.LogInformation("Runner instance {InstanceId} initialized on host {HostId} with profile {ProfileId}",
            this.GetPrimaryKeyString(), hostId, profileId);

        await SyncToDocumentDb();
        await PublishStatusChange();
        StartPendingTimer();
    }

    public async Task MarkDeployed()
    {
        _state.State.DeployedAt = DateTime.UtcNow;
        RecordStatusTransition(_state.State.Status, "Deployed to host", "grain_call");
        await _state.WriteStateAsync();

        _logger.LogInformation("Runner instance {InstanceId} deployed", this.GetPrimaryKeyString());

        CancelTimer(ref _pendingTimer);
        StartRegistrationTimer();
        await PublishStatusChange();
        await SyncToDocumentDb();
    }

    public async Task MarkStarting(string? statusMessage = null)
    {
        var wasActive = IsConsumingHostCapacity(_state.State.Status);
        _state.State.Status = RunnerInstanceStatus.Starting;
        if (statusMessage != null)
            _state.State.StatusMessage = statusMessage;

        RecordStatusTransition(RunnerInstanceStatus.Starting, statusMessage ?? "Starting", "grain_call");
        await _state.WriteStateAsync();

        if (!wasActive)
            await NotifyHostIncrement();

        _logger.LogInformation("Runner instance {InstanceId} starting", this.GetPrimaryKeyString());
        await PublishStatusChange();
        await SyncToDocumentDb();
    }

    public async Task MarkRunning(string? containerId = null, string? vmName = null, int? processId = null, string? statusMessage = null)
    {
        var wasActive = IsConsumingHostCapacity(_state.State.Status);
        _state.State.Status = RunnerInstanceStatus.Running;
        _state.State.ContainerId = containerId;
        _state.State.VmName = vmName;
        _state.State.ProcessId = processId;
        _state.State.StartedAt = DateTime.UtcNow;
        if (statusMessage != null)
            _state.State.StatusMessage = statusMessage;

        RecordStatusTransition(RunnerInstanceStatus.Running, statusMessage ?? "Running", "grain_call");
        await _state.WriteStateAsync();

        _logger.LogInformation("Runner instance {InstanceId} running", this.GetPrimaryKeyString());
        await PublishStatusChange();
        await SyncToDocumentDb();

        if (!wasActive)
            await NotifyHostIncrement();

        CancelTimer(ref _registrationTimer);

        if (IsDynamic())
            StartDynamicJobTimer();

        StartHealthCheckTimer();
    }

    public async Task MarkStopping()
    {
        _state.State.Status = RunnerInstanceStatus.Stopping;
        RecordStatusTransition(RunnerInstanceStatus.Stopping, "Stopping", "grain_call");
        await _state.WriteStateAsync();

        _logger.LogInformation("Runner instance {InstanceId} stopping", this.GetPrimaryKeyString());

        CancelAllTimers();
        StartStopTimer();
        await PublishStatusChange();
        await SyncToDocumentDb();
    }

    public async Task MarkStopped()
    {
        var wasActive = IsConsumingHostCapacity(_state.State.Status);
        _state.State.Status = RunnerInstanceStatus.Stopped;
        _state.State.StoppedAt = DateTime.UtcNow;
        RecordStatusTransition(RunnerInstanceStatus.Stopped, "Stopped", "grain_call");
        await _state.WriteStateAsync();

        _logger.LogInformation("Runner instance {InstanceId} stopped", this.GetPrimaryKeyString());

        CancelAllTimers();
        if (wasActive)
            await NotifyHostDecrement();
        await PublishStatusChange();
        await SyncToDocumentDb();

        await PurgeDynamicStateIfNeeded();
    }

    public async Task MarkFailed(string error)
    {
        var wasActive = IsConsumingHostCapacity(_state.State.Status);
        _state.State.Status = RunnerInstanceStatus.Failed;
        _state.State.ErrorMessage = error;
        RecordStatusTransition(RunnerInstanceStatus.Failed, error, "timer");
        await _state.WriteStateAsync();

        _logger.LogWarning("Runner instance {InstanceId} failed: {Error}", this.GetPrimaryKeyString(), error);

        CancelAllTimers();
        if (wasActive)
            await NotifyHostDecrement();
        await PublishStatusChange();
        await SyncToDocumentDb();

        await PurgeDynamicStateIfNeeded();
    }

    public async Task MarkCrashed(string reason)
    {
        var wasActive = IsConsumingHostCapacity(_state.State.Status);
        _state.State.Status = RunnerInstanceStatus.Crashed;
        _state.State.ErrorMessage = reason;
        RecordStatusTransition(RunnerInstanceStatus.Crashed, reason, "health_check");
        await _state.WriteStateAsync();

        _logger.LogWarning("Runner instance {InstanceId} crashed: {Reason}", this.GetPrimaryKeyString(), reason);

        CancelAllTimers();
        if (wasActive)
            await NotifyHostDecrement();
        await PublishStatusChange();
        await SyncToDocumentDb();

        await PurgeDynamicStateIfNeeded();
    }

    public async Task UpdateHealth(string? statusMessage = null)
    {
        _state.State.LastHealthCheck = DateTime.UtcNow;
        if (statusMessage != null)
            _state.State.StatusMessage = statusMessage;

        await _state.WriteStateAsync();

        // Reset health check timer
        CancelTimer(ref _healthTimer);
        StartHealthCheckTimer();
        await SyncToDocumentDb();
    }

    public async Task UpdateStatusMessage(string message)
    {
        _state.State.StatusMessage = message;
        await _state.WriteStateAsync();
        await PublishStatusChange();
        await SyncToDocumentDb();
    }

    public async Task DeployLocally(DeployRunnerCommand command)
    {
        _logger.LogInformation(
            "TODO: Local deployment of runner {Name} on host {Host} (backend: {Backend})",
            _state.State.RunnerName, _state.State.HostId, command.Backend);

        // In the future, this will call the local DockerBackend/TartBackend/NativeBackend
        // directly from the grain (when host-affinity placement is active).
        // For now, the existing Agent + SignalR path handles actual deployment.

        await MarkStarting("Deploying locally...");
    }

    // --- Timer callbacks ---

    private async Task OnPendingTimeout(CancellationToken ct)
    {
        if (_state.State.Status == RunnerInstanceStatus.Pending)
        {
            _logger.LogWarning("Runner instance {InstanceId} pending timeout", this.GetPrimaryKeyString());
            await MarkFailed("Deploy timeout — agent did not acknowledge within 2 minutes");
        }
    }

    private async Task OnRegistrationTimeout(CancellationToken ct)
    {
        if (_state.State.Status == RunnerInstanceStatus.Starting)
        {
            _logger.LogWarning("Runner instance {InstanceId} registration timeout", this.GetPrimaryKeyString());
            await MarkFailed("Registration timeout — runner did not connect to provider within 5 minutes");
        }
    }

    private async Task OnDynamicJobTimeout(CancellationToken ct)
    {
        if (_state.State.Status == RunnerInstanceStatus.Running && IsDynamic())
        {
            _logger.LogWarning("Runner instance {InstanceId} dynamic job timeout", this.GetPrimaryKeyString());
            await MarkFailed("Dynamic runner timeout — no completion webhook received within 2 hours");
        }
    }

    private async Task OnStopTimeout(CancellationToken ct)
    {
        if (_state.State.Status == RunnerInstanceStatus.Stopping)
        {
            _logger.LogWarning("Runner instance {InstanceId} stop timeout", this.GetPrimaryKeyString());
            await MarkFailed("Stop timeout — agent did not confirm stop within 5 minutes");
        }
    }

    private async Task OnHealthStale(CancellationToken ct)
    {
        var lastSeenAt = _state.State.LastHealthCheck ?? _state.State.StartedAt;

        if (_state.State.Status == RunnerInstanceStatus.Running
            && lastSeenAt.HasValue
            && (DateTime.UtcNow - lastSeenAt.Value).TotalMinutes >= 3)
        {
            _logger.LogWarning("Runner instance {InstanceId} health check stale", this.GetPrimaryKeyString());
            await MarkCrashed("Health check stale — runner may have crashed");
        }
    }

    // --- Timer management ---

    private void StartPendingTimer()
    {
        CancelTimer(ref _pendingTimer);
        _pendingTimer = this.RegisterGrainTimer<object?>(
            (_, ct) => OnPendingTimeout(ct),
            null,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(2),
                Period = Timeout.InfiniteTimeSpan
            });
    }

    private void StartRegistrationTimer()
    {
        CancelTimer(ref _registrationTimer);
        _registrationTimer = this.RegisterGrainTimer<object?>(
            (_, ct) => OnRegistrationTimeout(ct),
            null,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(5),
                Period = Timeout.InfiniteTimeSpan
            });
    }

    private void StartDynamicJobTimer()
    {
        CancelTimer(ref _dynamicTimer);
        _dynamicTimer = this.RegisterGrainTimer<object?>(
            (_, ct) => OnDynamicJobTimeout(ct),
            null,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromHours(2),
                Period = Timeout.InfiniteTimeSpan
            });
    }

    private void StartStopTimer()
    {
        CancelTimer(ref _stopTimer);
        _stopTimer = this.RegisterGrainTimer<object?>(
            (_, ct) => OnStopTimeout(ct),
            null,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(5),
                Period = Timeout.InfiniteTimeSpan
            });
    }

    private void StartHealthCheckTimer()
    {
        CancelTimer(ref _healthTimer);
        _healthTimer = this.RegisterGrainTimer<object?>(
            (_, ct) => OnHealthStale(ct),
            null,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(3),
                Period = Timeout.InfiniteTimeSpan
            });
    }

    private static void CancelTimer(ref IGrainTimer? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    private void CancelAllTimers()
    {
        CancelTimer(ref _pendingTimer);
        CancelTimer(ref _registrationTimer);
        CancelTimer(ref _dynamicTimer);
        CancelTimer(ref _stopTimer);
        CancelTimer(ref _healthTimer);
    }

    private async Task PublishStatusChange()
    {
        var streamProvider = this.GetStreamProvider("RunnerEvents");
        var streamId = StreamId.Create("RunnerStatus", "all");
        var stream = streamProvider.GetStream<RunnerStatusChangedEvent>(streamId);
        await stream.OnNextAsync(new RunnerStatusChangedEvent
        {
            InstanceId = this.GetPrimaryKeyString(),
            RunnerName = _state.State.RunnerName,
            HostId = _state.State.HostId,
            Status = _state.State.Status,
            StatusMessage = _state.State.StatusMessage
        });
    }

    // --- Helpers ---

    private bool IsDynamic() =>
        _state.State.ProvisioningMode.Equals("dynamic", StringComparison.OrdinalIgnoreCase)
        || _state.State.ProvisioningMode.Equals("webhook", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsumingHostCapacity(RunnerInstanceStatus status) =>
        status is RunnerInstanceStatus.Starting or RunnerInstanceStatus.Running or RunnerInstanceStatus.Stopping;

    private void RecordStatusTransition(RunnerInstanceStatus status, string? message, string source)
    {
        _state.State.StatusHistory.Add(new StatusHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Status = status,
            StatusMessage = message,
            Source = source
        });

        // Cap history at 50 entries
        if (_state.State.StatusHistory.Count > 50)
            _state.State.StatusHistory.RemoveRange(0, _state.State.StatusHistory.Count - 50);
    }

    private async Task SyncToDocumentDb()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            var instanceId = this.GetPrimaryKeyString();
            var existing = await store.Get<RunnerInstance>(instanceId);

            if (existing == null)
            {
                existing = new RunnerInstance
                {
                    Id = instanceId,
                    RunnerName = _state.State.RunnerName
                };
                ApplyProjection(existing);
                await store.Insert(existing);
            }
            else
            {
                ApplyProjection(existing);
                await store.Update(existing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync RunnerInstance to DocumentDB");
        }
    }

    private void ApplyProjection(RunnerInstance instance)
    {
        instance.HostId = _state.State.HostId;
        instance.ProfileId = _state.State.ProfileId;
        instance.RunnerName = _state.State.RunnerName;
        instance.Status = _state.State.Status;
        instance.StatusMessage = _state.State.StatusMessage;
        instance.ContainerId = _state.State.ContainerId;
        instance.VmName = _state.State.VmName;
        instance.ProcessId = _state.State.ProcessId;
        instance.ErrorMessage = _state.State.ErrorMessage;
        instance.ProvisioningMode = _state.State.ProvisioningMode;
        instance.WebhookEventId = _state.State.WebhookEventId;
        instance.JitConfig = _state.State.JitConfig;
        instance.JobId = _state.State.JobId;
        instance.ManagedByRunnerRunner = true;
        instance.CreatedAt = _state.State.CreatedAt;
        instance.DeployedAt = _state.State.DeployedAt;
        instance.StartedAt = _state.State.StartedAt;
        instance.StoppedAt = _state.State.StoppedAt;
        instance.LastHealthCheck = _state.State.LastHealthCheck;
        instance.ProvisioningRuleId = _state.State.ProvisioningRuleId;
        instance.StatusHistory = _state.State.StatusHistory
            .Select(e => new StatusHistoryEntry
            {
                Timestamp = e.Timestamp,
                Status = e.Status,
                StatusMessage = e.StatusMessage,
                Source = e.Source
            }).ToList();
    }

    private async Task NotifyHostIncrement()
    {
        if (string.IsNullOrEmpty(_state.State.HostId))
            return;

        try
        {
            var hostGrain = GrainFactory.GetGrain<IHostGrain>(_state.State.HostId);
            await hostGrain.IncrementRunningCount(_state.State.Backend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify host {HostId} of runner start for instance {InstanceId}",
                _state.State.HostId, this.GetPrimaryKeyString());
        }
    }

    private async Task NotifyHostDecrement()
    {
        if (string.IsNullOrEmpty(_state.State.HostId))
            return;

        try
        {
            var hostGrain = GrainFactory.GetGrain<IHostGrain>(_state.State.HostId);
            await hostGrain.DecrementRunningCount(_state.State.Backend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify host {HostId} of runner stop for instance {InstanceId}",
                _state.State.HostId, this.GetPrimaryKeyString());
        }
    }

    private async Task PurgeDynamicStateIfNeeded()
    {
        if (!IsDynamic())
            return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var cleanupService = scope.ServiceProvider.GetRequiredService<RunnerRegistrationCleanupService>();

            await cleanupService.TryRemoveRunnerAsync(store, new RunnerInstance
            {
                Id = this.GetPrimaryKeyString(),
                RunnerName = _state.State.RunnerName,
                HostId = _state.State.HostId,
                ProfileId = _state.State.ProfileId,
                ProvisioningMode = _state.State.ProvisioningMode,
                WebhookEventId = _state.State.WebhookEventId,
                JobId = _state.State.JobId,
                Status = _state.State.Status
            });

            // Keep the DocumentDB entry for job history; only remove provider registration
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up dynamic runner registration for {InstanceId}", this.GetPrimaryKeyString());
        }

        try
        {
            await _state.ClearStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear persistent grain state for dynamic instance {InstanceId}", this.GetPrimaryKeyString());
        }

        DeactivateOnIdle();
    }
}
