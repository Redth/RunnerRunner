using RunnerRunner.Server.Components;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Hubs;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Webhooks;
using RunnerRunner.Core.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults (OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Document store (Shiny DocumentDB with SQLite)
var dbPath = builder.Configuration.GetValue<string>("Database:Path");
if (string.IsNullOrEmpty(dbPath))
{
    var dataDir = Path.Combine(builder.Environment.ContentRootPath, ".db");
    Directory.CreateDirectory(dataDir);
    dbPath = Path.Combine(dataDir, "runnerrunner.db");
}
else
{
    var dir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);
}
builder.Services.AddRunnerRunnerDocumentStore($"Data Source={dbPath}");

// SignalR for agent communication
builder.Services.AddSignalR();

// HTTP client for provider APIs
builder.Services.AddHttpClient();

// Orleans silo (co-hosted with Blazor server)
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage("Default");
    silo.AddMemoryGrainStorage("PersistentStore");
    silo.UseInMemoryReminderService();
});

// Runner providers
builder.Services.AddSingleton<IRunnerProviderPlugin, GitHubActionsProvider>();
builder.Services.AddSingleton<IRunnerProviderPlugin, GiteaActionsProvider>();
builder.Services.AddSingleton<IRunnerProviderPlugin, AzDoAgentProvider>();

// Services
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<JitConfigService>();

// Background services
builder.Services.AddHostedService<OrchestrationEngine>();
builder.Services.AddHostedService<VersionCheckService>();
builder.Services.AddHostedService<DynamicProvisioningService>();
builder.Services.AddHostedService<ReconciliationService>();
builder.Services.AddHostedService<RunnerTimeoutService>();

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

// Aspire health check endpoints
app.MapDefaultEndpoints();

app.Run();
