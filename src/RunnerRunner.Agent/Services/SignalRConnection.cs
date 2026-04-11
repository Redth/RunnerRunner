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

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public SignalRConnection(ILogger<SignalRConnection> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var serverUrl = _configuration["RunnerRunner:ServerUrl"]
            ?? throw new InvalidOperationException("RunnerRunner:ServerUrl configuration is required");

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/agent";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                var token = _configuration["RunnerRunner:AgentToken"];
                if (!string.IsNullOrEmpty(token))
                {
                    options.Headers.Add("X-Agent-Token", token);
                }

                // Force IPv4 to avoid "No route to host" under macOS launchd
                options.HttpMessageHandlerFactory = _ => new SocketsHttpHandler
                {
                    ConnectCallback = async (context, ct) =>
                    {
                        var socket = new System.Net.Sockets.Socket(
                            System.Net.Sockets.AddressFamily.InterNetwork,
                            System.Net.Sockets.SocketType.Stream,
                            System.Net.Sockets.ProtocolType.Tcp);
                        await socket.ConnectAsync(context.DnsEndPoint.Host,
                            context.DnsEndPoint.Port, ct);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                };
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        // Register server → agent handlers
        _connection.On<DeployRunnerCommand>("DeployRunner", async cmd =>
        {
            if (OnDeployRunner != null) await OnDeployRunner(cmd);
        });

        _connection.On<StopRunnerCommand>("StopRunner", async cmd =>
        {
            if (OnStopRunner != null) await OnStopRunner(cmd);
        });

        _connection.On<SyncDesiredStateCommand>("SyncDesiredState", async cmd =>
        {
            if (OnSyncDesiredState != null) await OnSyncDesiredState(cmd);
        });

        _connection.On<PullImageCommand>("PullImage", async cmd =>
        {
            if (OnPullImage != null) await OnPullImage(cmd);
        });

        _connection.On<ListImagesCommand>("ListImages", async cmd =>
        {
            if (OnListImages != null) await OnListImages(cmd);
        });

        _connection.On<DeleteImageCommand>("DeleteImage", async cmd =>
        {
            if (OnDeleteImage != null) await OnDeleteImage(cmd);
        });

        _connection.On<LoginRegistryCommand>("LoginRegistry", async cmd =>
        {
            if (OnLoginRegistry != null) await OnLoginRegistry(cmd);
        });

        _connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "Connection lost, reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Reconnected with connection ID: {ConnectionId}", connectionId);
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            _logger.LogWarning(error, "Connection closed");
            return Task.CompletedTask;
        };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _connection.StartAsync(ct);
                _logger.LogInformation("Connected to RunnerRunner server at {Url}", hubUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to server, retrying in 5 seconds...");
                await Task.Delay(5000, ct);
            }
        }
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
