using Microsoft.AspNetCore.SignalR.Client;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Manages the persistent SignalR connection to the RunnerRunner server.
/// Handles reconnection, authentication, and message routing.
/// </summary>
public class SignalRConnection : IAsyncDisposable
{
    private readonly ILogger<SignalRConnection> _logger;
    private readonly IConfiguration _configuration;
    private HubConnection? _connection;

    public event Func<DeployRunnerCommand, Task>? OnDeployRunner;
    public event Func<StopRunnerCommand, Task>? OnStopRunner;
    public event Func<SyncDesiredStateCommand, Task>? OnSyncDesiredState;
    public event Func<PullImageCommand, Task>? OnPullImage;
    public event Func<ListImagesCommand, Task>? OnListImages;
    public event Func<DeleteImageCommand, Task>? OnDeleteImage;
    public event Func<LoginRegistryCommand, Task>? OnLoginRegistry;
    public event Func<Task>? OnGetHostEnvironment;
    public event Func<GetHostLogsCommand, Task>? OnGetHostLogs;
    public event Func<GetRunnerLogsCommand, Task>? OnGetRunnerLogs;
    public event Func<CleanupOrphanCommand, Task>? OnCleanupOrphan;
    public event Func<Task>? OnReconnected;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public SignalRConnection(ILogger<SignalRConnection> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var serverUrl = _configuration["RunnerRunner:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _logger.LogWarning("RunnerRunner:ServerUrl is not configured; host execution bridge will stay offline");
            return;
        }

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/agent";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Recreate connection for each attempt to avoid stale state
                _connection = BuildConnection(hubUrl);
                await _connection.StartAsync(ct);
                _logger.LogInformation("Connected to RunnerRunner server at {Url}", hubUrl);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to connect to server, retrying in 5 seconds... {Error}", ex.Message);
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private HubConnection BuildConnection(string hubUrl)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                var token = _configuration["RunnerRunner:AgentToken"];
                if (!string.IsNullOrEmpty(token))
                {
                    options.Headers.Add("X-Agent-Token", token);
                }

                // Use ServerSentEvents transport — WebSockets fail under macOS launchd
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        connection.On<DeployRunnerCommand>("DeployRunner", async cmd =>
        {
            if (OnDeployRunner != null) await OnDeployRunner(cmd);
        });

        connection.On<StopRunnerCommand>("StopRunner", async cmd =>
        {
            if (OnStopRunner != null) await OnStopRunner(cmd);
        });

        connection.On<SyncDesiredStateCommand>("SyncDesiredState", async cmd =>
        {
            if (OnSyncDesiredState != null) await OnSyncDesiredState(cmd);
        });

        connection.On<PullImageCommand>("PullImage", async cmd =>
        {
            if (OnPullImage != null) await OnPullImage(cmd);
        });

        connection.On<ListImagesCommand>("ListImages", async cmd =>
        {
            if (OnListImages != null) await OnListImages(cmd);
        });

        connection.On<DeleteImageCommand>("DeleteImage", async cmd =>
        {
            if (OnDeleteImage != null) await OnDeleteImage(cmd);
        });

        connection.On<LoginRegistryCommand>("LoginRegistry", async cmd =>
        {
            if (OnLoginRegistry != null) await OnLoginRegistry(cmd);
        });

        connection.On("GetHostEnvironment", async () =>
        {
            if (OnGetHostEnvironment != null) await OnGetHostEnvironment();
        });

        connection.On<GetHostLogsCommand>("GetHostLogs", async cmd =>
        {
            if (OnGetHostLogs != null) await OnGetHostLogs(cmd);
        });

        connection.On<GetRunnerLogsCommand>("GetRunnerLogs", async cmd =>
        {
            if (OnGetRunnerLogs != null) await OnGetRunnerLogs(cmd);
        });

        connection.On<CleanupOrphanCommand>("CleanupOrphan", async cmd =>
        {
            if (OnCleanupOrphan != null) await OnCleanupOrphan(cmd);
        });

        connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "Connection lost, reconnecting...");
            return Task.CompletedTask;
        };

        connection.Reconnected += async connectionId =>
        {
            _logger.LogInformation("Reconnected with connection ID: {ConnectionId}", connectionId);
            if (OnReconnected != null) await OnReconnected();
        };

        connection.Closed += error =>
        {
            _logger.LogWarning(error, "Connection closed");
            return Task.CompletedTask;
        };

        return connection;
    }

    public async Task SendAgentConnected(AgentInfo info)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("AgentConnected", info);
    }

    public async Task SendRunnerStarted(RunnerStartedEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("RunnerStarted", evt);
    }

    public async Task SendRunnerStopped(RunnerStoppedEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("RunnerStopped", evt);
    }

    public async Task SendHeartbeat(HeartbeatEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("Heartbeat", evt);
    }

    public async Task SendRunnerHealthUpdate(RunnerHealthUpdateEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("RunnerHealthUpdate", evt);
    }

    public async Task SendImageList(ImageListEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("ImageListResponse", evt);
    }

    public async Task SendImageRefreshStatus(ImageRefreshStatusEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("ImageRefreshStatus", evt);
    }

    public async Task SendImagePullProgress(ImagePullProgressEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("ImagePullProgress", evt);
    }

    public async Task SendImagePullComplete(ImagePullCompleteEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("ImagePullComplete", evt);
    }

    public async Task SendImageDeleted(ImageDeletedEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("ImageDeleted", evt);
    }

    public async Task SendHostEnvironment(HostEnvironmentEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("HostEnvironmentResponse", evt);
    }

    public async Task SendRunnerDiscovery(RunnerDiscoveryEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("RunnerDiscovery", evt);
    }

    public async Task SendRunnerLogs(RunnerLogsEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("RunnerLogsResponse", evt);
    }

    public async Task SendHostLogs(HostLogsEvent evt)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("HostLogsResponse", evt);
    }

    public async Task SendReconciliation(ReconciliationReport report)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("Reconciliation", report);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
            return Delays[index];
        }
    }
}
