using RunnerRunner.Server.Data;
using Orleans.Runtime.MembershipService.SiloMetadata;
using RunnerRunner.Agent.Services;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "RunnerRunner HostSilo";
});

// Configuration
var identity = RunnerRunner.HostSilo.HostSiloIdentityResolver.Resolve(builder.Configuration);

var pgConnectionString = builder.Configuration["Database:ConnectionString"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner";

// DocumentDB (PostgreSQL) — for grain projections
builder.Services.AddRunnerRunnerDocumentStore(pgConnectionString);

// Host registration service
builder.Services.AddHostedService<RunnerRunner.HostSilo.HostRegistrationService>();

// Host-local execution: HostSilo receives Orleans stream commands and controls
// Docker/Tart/native backends directly on this machine.
builder.Services.AddSingleton<RunnerLifecycleManager>();
builder.Services.AddSingleton<HealthReporter>();
builder.Services.AddSingleton<ImageManager>();
builder.Services.AddHostedService<RunnerRunner.HostSilo.HostCommandService>();

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
        ["hostId"] = identity.HostId,
        ["hostName"] = identity.HostName,
        ["platform"] = identity.Platform.ToString(),
        ["architecture"] = identity.Architecture
    });
});

var host = builder.Build();
host.Run();
