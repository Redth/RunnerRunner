using RunnerRunner.Server.Components;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Hubs;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Webhooks;
using RunnerRunner.Core.Interfaces;
using Orleans.Dashboard;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults (OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Document store (Shiny DocumentDB with PostgreSQL)
var pgConnectionString = builder.Configuration.GetValue<string>("Database:ConnectionString")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner";
builder.Services.AddRunnerRunnerDocumentStore(pgConnectionString);

// SignalR for agent communication
builder.Services.AddSignalR();

// HTTP client for provider APIs
builder.Services.AddHttpClient();

// Orleans silo (co-hosted with Blazor server)
builder.UseOrleans(silo =>
{
    var orleansConnString = pgConnectionString;

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
builder.Services.AddSingleton<JitConfigService>();

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
builder.Services.AddHostedService<StreamSubscriptionService>();
builder.Services.AddHostedService<ServerHostRegistrationService>();
// builder.Services.AddHostedService<ReconciliationService>();    // → HostGrain + RunnerInstanceGrain
// builder.Services.AddHostedService<RunnerTimeoutService>();     // → RunnerInstanceGrain timers

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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

// SignalR hub for agent connections
app.MapHub<AgentHub>("/hubs/agent");

// Webhook endpoints for GitHub/Gitea workflow_job events
app.MapWebhookEndpoints();

// Orleans Dashboard
app.MapOrleansDashboard("/orleans");

// Aspire health check endpoints
app.MapDefaultEndpoints();

app.Run();
