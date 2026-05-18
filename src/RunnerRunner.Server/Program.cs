using RunnerRunner.Server.Components;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Hubs;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using RunnerRunner.Server.Webhooks;
using RunnerRunner.Core.Interfaces;
using Orleans.Dashboard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using RunnerRunner.Server.Services.Logs;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
    });
});

// Aspire service defaults (OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults();

if (!builder.Environment.IsDevelopment())
{
    var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
    if (string.IsNullOrWhiteSpace(dataProtectionKeysPath))
        dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "data", "data-protection-keys");

    Directory.CreateDirectory(dataProtectionKeysPath);
    builder.Services.AddDataProtection()
        .SetApplicationName("RunnerRunner.Server")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Document store (Shiny DocumentDB with PostgreSQL)
var pgConnectionString = builder.Configuration.GetValue<string>("Database:ConnectionString")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner";
builder.Services.AddRunnerRunnerDocumentStore(pgConnectionString);

// SignalR remains available only for legacy log/image compatibility surfaces.
builder.Services.AddSignalR();
builder.Services.AddGrpc();

// HTTP client for provider APIs
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// Orleans silo (co-hosted with Blazor server)
builder.UseOrleans(silo =>
{
    var orleansConnString = pgConnectionString;

    silo.Configure<Orleans.Hosting.ReminderOptions>(options =>
    {
        options.MinimumReminderPeriod = TimeSpan.FromSeconds(30);
    });

    if (builder.Environment.IsDevelopment())
    {
        // Dev: localhost clustering + in-memory storage
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage("Default");
        silo.AddMemoryGrainStorage("PersistentStore");
        silo.AddMemoryGrainStorage("PubSubStore");
        silo.UseInMemoryReminderService();
    }
    else
    {
        // Production: PostgreSQL for clustering, persistence, reminders
        silo.UseAdoNetClustering(options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = orleansConnString;
        });
        silo.AddAdoNetGrainStorage("Default", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = orleansConnString;
        });
        silo.AddAdoNetGrainStorage("PersistentStore", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = orleansConnString;
        });
        silo.AddAdoNetGrainStorage("PubSubStore", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = orleansConnString;
        });
        silo.UseAdoNetReminderService(options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = orleansConnString;
        });

        // Advertise external IP so silos on other machines can reach us via mapped ports
        var advertisedIp = builder.Configuration["Orleans:AdvertisedIPAddress"];
        if (!string.IsNullOrEmpty(advertisedIp) && System.Net.IPAddress.TryParse(advertisedIp, out var ip))
        {
            silo.Configure<Orleans.Configuration.EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = ip;
                options.SiloListeningEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 11111);
                options.GatewayListeningEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 30000);
                options.SiloPort = 11111;
                options.GatewayPort = 30000;
            });
        }
    }

    silo.AddMemoryStreams("RunnerEvents");
    // Propagate distributed traces through grain calls for OTEL
    silo.AddActivityPropagation();
    // Orleans built-in dashboard (Orleans 10+)
    silo.AddDashboard();
});

// Runner providers
builder.Services.AddSingleton<IRunnerProviderPlugin, GitHubActionsProvider>();
builder.Services.AddSingleton<IRunnerProviderPlugin, GiteaActionsProvider>();
builder.Services.AddSingleton<IRunnerProviderPlugin, AzDoAgentProvider>();

// Services
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<GitHubAuthenticationService>();
builder.Services.AddSingleton<JitConfigService>();
builder.Services.AddSingleton<RunnerRegistrationCleanupService>();
builder.Services.AddSingleton<IRegistryCatalogService, RegistryCatalogService>();
builder.Services.AddSingleton<HostWorkerConnectionRegistry>();
builder.Services.AddSingleton<HostWorkerLogCache>();
builder.Services.AddSingleton<ObservedLogStore>();
builder.Services.AddSingleton<ILoggerProvider, ObservedLoggerProvider>();
builder.Services.AddScoped<ObservedLogQueryService>();
builder.Services.AddSingleton<HostWorkerLocalUpdateStore>();
builder.Services.AddSingleton<HostWorkerUpdateService>();
builder.Services.AddSingleton<HostWorkerEventProcessor>();
builder.Services.AddSingleton<LongRunningTaskService>();
builder.Services.AddSingleton<IHostCommandDispatcher, GrpcHostCommandDispatcher>();
builder.Services.AddSingleton<ProvisioningRuleGrainSyncService>();
builder.Services.AddScoped<CapacityPlanningService>();

// === Orleans Grain Architecture (Phase 5) ===
// The following legacy services are replaced by Orleans grains:
//   RunnerTimeoutService → RunnerInstanceGrain (grain timers)
//   ReconciliationService → HostGrain (heartbeat) + agent reconciliation loop
//
// These services are still active during the migration:
//   OrchestrationEngine → will be replaced by ProvisioningRuleGrain
//   DynamicProvisioningService → will be replaced by ProvisioningRuleGrain + WebhookProcessorGrain
//   VersionCheckService → no grain equivalent yet

// Background services
builder.Services.AddHostedService<OrchestrationEngine>();
builder.Services.AddHostedService<VersionCheckService>();
builder.Services.AddHostedService<DynamicProvisioningService>();
builder.Services.AddHostedService<ProvisioningRuleGrainStartupSyncService>();
builder.Services.AddHostedService<StreamSubscriptionService>();
builder.Services.AddHostedService<GitHubRunnerSweepService>();
builder.Services.AddHostedService<ReconciliationService>();    // extra stale/orphan cleanup during migration
builder.Services.AddHostedService<RunnerTimeoutService>();     // catches stuck pre-registration / stale instances

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
app.LogRunnerRunnerStartup();

// Ensure Orleans ADO.NET backing tables exist before the silo starts.
if (!app.Environment.IsDevelopment())
{
    var schemaLogger = app.Services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("RunnerRunner.Server.Data.OrleansSchemaInitializer");
    await OrleansSchemaInitializer.EnsureCreatedAsync(pgConnectionString, schemaLogger);
}

// Ensure all document store tables exist before serving requests
await DatabaseInitializer.EnsureTablesCreatedAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Legacy compatibility hub; HostWorker runtime commands use gRPC.
app.MapHub<AgentHub>("/hubs/agent");
app.MapGrpcService<HostWorkerGrpcService>();
app.MapHostWorkerUpdateEndpoints();

// Webhook endpoints for GitHub/Gitea workflow_job events
app.MapWebhookEndpoints();

// Orleans Dashboard
app.MapOrleansDashboard("/orleans");

// Aspire health check endpoints
app.MapDefaultEndpoints();

app.Run();
