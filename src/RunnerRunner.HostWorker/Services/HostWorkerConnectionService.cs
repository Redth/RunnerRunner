using Grpc.Core;
using Grpc.Net.Client;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using System.Net;
using System.Threading.Channels;

namespace RunnerRunner.HostWorker.Services;

internal sealed class HostWorkerConnectionService : BackgroundService, IHostWorkerEventSink
{
    private readonly IConfiguration _configuration;
    private readonly HostWorkerIdentity _identity;
    private readonly HostCommandProcessor _processor;
    private readonly RunnerLifecycleManager _lifecycleManager;
    private readonly HostWorkerLogPublisher _logPublisher;
    private readonly ILogger<HostWorkerConnectionService> _logger;
    private readonly HostResourceUsageCollector _resourceUsageCollector;
    private readonly Channel<HostWorkerMessage> _outbound = Channel.CreateBounded<HostWorkerMessage>(
        new BoundedChannelOptions(1_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private long _sequence;

    public HostWorkerConnectionService(
        IConfiguration configuration,
        HostWorkerIdentity identity,
        HostCommandProcessor processor,
        RunnerLifecycleManager lifecycleManager,
        HostWorkerLogPublisher logPublisher,
        ILogger<HostWorkerConnectionService> logger,
        HostResourceUsageCollector resourceUsageCollector)
    {
        _configuration = configuration;
        _identity = identity;
        _processor = processor;
        _lifecycleManager = lifecycleManager;
        _logPublisher = logPublisher;
        _logger = logger;
        _resourceUsageCollector = resourceUsageCollector;
        _processor.AttachEventSink(this);
        _logPublisher.AttachEventSink(this);
    }

    public ValueTask PublishAsync(HostWorkerMessage message, CancellationToken cancellationToken = default)
        => _outbound.Writer.WriteAsync(message, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverUrl = _configuration["HostWorker:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _logger.LogError("HostWorker:ServerUrl is required. The worker will stay alive and retry configuration.");
            serverUrl = "http://localhost:5000";
        }

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                attempt++;
                await RunConnectionAsync(serverUrl, stoppingToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, attempt))));
                _logger.LogWarning(ex, "HostWorker connection failed. Retrying in {DelaySeconds:n0}s", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task RunConnectionAsync(string serverUrl, CancellationToken stoppingToken)
    {
        if (serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        _logger.LogInformation("Connecting HostWorker {HostId} to {ServerUrl}", _identity.HostId, serverUrl);

        using var channel = GrpcChannel.ForAddress(serverUrl, CreateGrpcChannelOptions(serverUrl));
        var client = new HostWorkerControl.HostWorkerControlClient(channel);
        using var call = client.Connect(cancellationToken: stoppingToken);

        await call.RequestStream.WriteAsync(CreateHello(), stoppingToken);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sendLoop = SendLoopAsync(call.RequestStream, linkedCts.Token);
        var receiveLoop = ReceiveLoopAsync(call.ResponseStream, linkedCts.Token);
        var heartbeatLoop = HeartbeatLoopAsync(linkedCts.Token);

        var completed = await Task.WhenAny(sendLoop, receiveLoop, heartbeatLoop);
        linkedCts.Cancel();

        try
        {
            await completed;
        }
        finally
        {
            try
            {
                await Task.WhenAll(sendLoop, receiveLoop, heartbeatLoop);
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
            }
        }
    }

    private GrpcChannelOptions CreateGrpcChannelOptions(string serverUrl)
    {
        var proxyUrl = ResolveProxyUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return new GrpcChannelOptions();

        var proxy = new WebProxy(proxyUrl)
        {
            BypassProxyOnLocal = true
        };
        var bypassList = CreateProxyBypassList(_configuration["HostWorker:NoProxy"]);
        if (bypassList.Length > 0)
            proxy.BypassList = bypassList;

        return new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = proxy
            }
        };
    }

    private string? ResolveProxyUrl(string serverUrl)
    {
        var httpProxy = _configuration["HostWorker:HttpProxy"];
        var httpsProxy = _configuration["HostWorker:HttpsProxy"];
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(httpsProxy) ? httpProxy : httpsProxy;
        }

        return string.IsNullOrWhiteSpace(httpProxy) ? httpsProxy : httpProxy;
    }

    private static string[] CreateProxyBypassList(string? noProxy)
        => string.IsNullOrWhiteSpace(noProxy)
            ? []
            : noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .ToArray();

    private async Task SendLoopAsync(IClientStreamWriter<HostWorkerMessage> requestStream, CancellationToken ct)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(ct))
            await requestStream.WriteAsync(message, ct);
    }

    private async Task ReceiveLoopAsync(IAsyncStreamReader<HostWorkerMessage> responseStream, CancellationToken ct)
    {
        while (await responseStream.MoveNext(ct))
        {
            var message = responseStream.Current;
            if (message.Kind == HostWorkerMessageKinds.Command)
            {
                await _processor.EnqueueAsync(message, ct);
                continue;
            }

            _logger.LogDebug("Ignoring unsupported server message kind {Kind}", message.Kind);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var resourceUsage = await _resourceUsageCollector.CollectAsync("heartbeat", ct);
            await PublishAsync(HostWorkerProtocol.CreateMessage(
                _identity.HostId,
                HostWorkerMessageKinds.Heartbeat,
                new HeartbeatEvent
                {
                    AgentId = _identity.HostId,
                    RunningInstanceCount = _lifecycleManager.RunningInstances.Count,
                    ResourceUsage = resourceUsage
                },
                sequence: Interlocked.Increment(ref _sequence)), ct);
        }
    }

    internal HostWorkerMessage CreateHello()
    {
        var capabilities = DetectCapabilities();
        var buildInfo = HostWorkerVersion.BuildInfo;
        var hello = new HostWorkerHello
        {
            Agent = new AgentInfo
            {
                AgentId = _identity.HostId,
                Name = _identity.HostName,
                Platform = _identity.Platform,
                Architecture = _identity.Architecture,
                AgentVersion = buildInfo.InformationalVersion,
                AgentCommitSha = buildInfo.CommitSha,
                AgentBuildTag = buildInfo.BuildTag,
                Capabilities = capabilities.ToList(),
                Runtime = HostWorkerRuntimeDetector.Detect(_configuration)
            },
            EnrollmentToken = _configuration["HostWorker:EnrollmentToken"],
            Labels = new Dictionary<string, string>
            {
                ["os"] = _identity.Platform.ToString().ToLowerInvariant(),
                ["arch"] = _identity.Architecture.ToLowerInvariant()
            }
        };

        return HostWorkerProtocol.CreateMessage(
            _identity.HostId,
            HostWorkerMessageKinds.Hello,
            hello,
            sequence: Interlocked.Increment(ref _sequence));
    }

    private IReadOnlyCollection<string> DetectCapabilities()
        => DetectCapabilities(_identity, _configuration["DOCKER_HOST"], File.Exists, ToolExists);

    internal static IReadOnlyCollection<string> DetectCapabilities(
        HostWorkerIdentity identity,
        string? dockerHost,
        Func<string, bool> fileExists,
        Func<string, bool> toolExists)
    {
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "native"
        };

        if (!string.IsNullOrWhiteSpace(dockerHost)
            || fileExists("/var/run/docker.sock")
            || toolExists("docker"))
        {
            capabilities.Add("docker");
        }

        if (identity.Platform == HostPlatform.MacOS && toolExists("tart"))
        {
            capabilities.Add("tart");
        }

        return capabilities;
    }

    private static bool ToolExists(string command)
    {
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var pathPart in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(pathPart, command);
            if (File.Exists(candidate))
                return true;
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return true;
        }

        return false;
    }
}
