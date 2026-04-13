using RunnerRunner.Core.Models;
using RunnerRunner.Server.Data;
using Orleans.Runtime.MembershipService.SiloMetadata;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

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
