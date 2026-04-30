using RunnerRunner.Core.Models;
using RunnerRunner.Server.Data;
using Orleans.Runtime.MembershipService.SiloMetadata;
using RunnerRunner.Agent;
using RunnerRunner.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

// Don't tear down the entire silo when a single BackgroundService throws.
// Transient SignalR/Orleans exceptions (e.g. TaskCanceledException during a
// reconnect) used to crash the host; the deploy script doesn't auto-restart
// it, so a single hiccup left the mac silo offline for hours.
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior =
        BackgroundServiceExceptionBehavior.Ignore;
});

// Configuration
var hostId = builder.Configuration["HostSilo:HostId"] ?? Environment.MachineName;
var hostName = builder.Configuration["HostSilo:HostName"] ?? hostId;
var platform = Enum.TryParse<HostPlatform>(builder.Configuration["HostSilo:Platform"], true, out var p) ? p : HostPlatform.Linux;
var architecture = builder.Configuration["HostSilo:Architecture"] ?? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

var pgConnectionString = builder.Configuration["Database:ConnectionString"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner";

// DocumentDB (PostgreSQL) — for grain projections
builder.Services.AddRunnerRunnerDocumentStore(pgConnectionString);

// Host registration service
builder.Services.AddHostedService<RunnerRunner.HostSilo.HostRegistrationService>();

// Host execution bridge: lets HostSilo receive deploy/stop/log commands from the server
// without needing a separate legacy Agent process.
builder.Services.AddSingleton<SignalRConnection>();
builder.Services.AddSingleton<RunnerLifecycleManager>();
builder.Services.AddSingleton<HealthReporter>();
builder.Services.AddSingleton<ImageManager>();
builder.Services.AddHostedService<AgentService>();

// Orleans silo (headless — no web UI)
builder.UseOrleans(silo =>
{
    if (builder.Environment.IsDevelopment())
    {
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage("Default");
        silo.AddMemoryGrainStorage("PersistentStore");
        silo.AddMemoryGrainStorage("PubSubStore");
        silo.UseInMemoryReminderService();
    }
    else
    {
        silo.UseAdoNetClustering(options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = pgConnectionString;
        });
        silo.AddAdoNetGrainStorage("Default", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = pgConnectionString;
        });
        silo.AddAdoNetGrainStorage("PersistentStore", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = pgConnectionString;
        });
        silo.AddAdoNetGrainStorage("PubSubStore", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = pgConnectionString;
        });
        silo.UseAdoNetReminderService(options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = pgConnectionString;
        });

        // Advertise external IP so silos on other machines can reach us
        var advertisedIp = builder.Configuration["Orleans:AdvertisedIPAddress"];
        if (!string.IsNullOrEmpty(advertisedIp) && System.Net.IPAddress.TryParse(advertisedIp, out var ip))
        {
            var siloPort = builder.Configuration.GetValue("Orleans:SiloPort", 11111);
            var gatewayPort = builder.Configuration.GetValue("Orleans:GatewayPort", 30000);
            silo.Configure<Orleans.Configuration.EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = ip;
                options.SiloListeningEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, siloPort);
                options.GatewayListeningEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, gatewayPort);
                options.SiloPort = siloPort;
                options.GatewayPort = gatewayPort;
            });
        }
    }

    silo.AddMemoryStreams("RunnerEvents");

    // Silo metadata for grain placement
    silo.UseSiloMetadata(new Dictionary<string, string>
    {
        ["hostId"] = hostId,
        ["hostName"] = hostName,
        ["platform"] = platform.ToString(),
        ["architecture"] = architecture
    });
});

var host = builder.Build();
host.Run();
