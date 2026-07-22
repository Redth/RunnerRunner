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
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RunnerRunner.Server.Authentication;
using RunnerRunner.Server.Data.Auth;
using RunnerRunner.Server.Services.Auth;
using Microsoft.Extensions.Logging;
using Radzen;
using RunnerRunner.Server.Services.Logs;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
    });
});

// When running behind a reverse proxy (NPM/Traefik/Caddy), enable forwarded headers
// so that HTTPS redirect, cookies, and Blazor WebSocket URLs are correct.
// Set REVERSE_PROXY_ENABLED=true in the container environment.
if (builder.Configuration.GetValue<bool>("REVERSE_PROXY_ENABLED"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        options.RequireHeaderSymmetry = false;
    });
}

// Aspire service defaults (OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults();

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "data", "data-protection-keys");

Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("RunnerRunner.Server")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

// Document store (Shiny DocumentDB with PostgreSQL)
var pgConnectionString = builder.Configuration.GetValue<string>("Database:ConnectionString")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner";
builder.Services.AddRunnerRunnerDocumentStore(pgConnectionString);

builder.Services.AddDbContext<RunnerRunnerAuthDbContext>(options =>
    options.UseNpgsql(pgConnectionString));

builder.Services
    .AddIdentity<RunnerRunnerUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<RunnerRunnerAuthDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "RunnerRunner.Auth";
    options.LoginPath = "/auth/login";
    options.LogoutPath = "/auth/logout";
    options.AccessDeniedPath = "/auth/access-denied";
    options.SlidingExpiration = true;
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
})
    .AddOpenIdConnect(RunnerRunnerAuthSchemes.Oidc, _ => { });
builder.Services.AddAuthorization(options => options.AddRunnerRunnerPolicies());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRadzenComponents();
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, RunnerRunnerOidcOptionsConfigurator>();
builder.Services.AddSingleton<RunnerRunnerAuthSettingsService>();
builder.Services.AddScoped<FirstRunSetupService>();

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
builder.Services.AddSingleton<HostWorkerEnrollmentGuideBuilder>();
builder.Services.AddSingleton<HostWorkerSshSetupService>();
builder.Services.AddSingleton<LongRunningTaskService>();
builder.Services.AddScoped<SettingsBackupService>();
builder.Services.AddSingleton<IHostCommandDispatcher, GrpcHostCommandDispatcher>();
builder.Services.AddSingleton<ProvisioningRuleGrainSyncService>();
builder.Services.AddScoped<CapacityPlanningService>();
builder.Services.AddScoped<DemoDataSeeder>();

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
builder.Services.AddHostedService<HostWorkerUpdateDrainService>();

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
await RunnerRunnerAuthSchemaInitializer.EnsureCreatedAsync(app.Services);
await app.Services.GetRequiredService<RunnerRunnerAuthSettingsService>().LoadAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseForwardedHeaders();
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseMiddleware<FirstRunSetupMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapRunnerRunnerAuthEndpoints();

// Legacy compatibility hub; HostWorker runtime commands use gRPC.
app.MapHub<AgentHub>("/hubs/agent");
app.MapGrpcService<HostWorkerGrpcService>();
app.MapHostWorkerUpdateEndpoints();

// Webhook endpoints for GitHub/Gitea workflow_job events
app.MapWebhookEndpoints();

// Orleans Dashboard
app.MapOrleansDashboard("/orleans")
    .RequireAuthorization(RunnerRunnerPolicies.ManageUsers);

// Aspire health check endpoints
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    var demo = app.MapGroup("/dev/demo-data")
        .RequireAuthorization(RunnerRunnerPolicies.ManageUsers);

    demo.MapPost("/seed", async (DemoDataSeeder seeder) =>
    {
        var result = await seeder.SeedAsync();
        return Results.Ok(new
        {
            message = "Demo data seeded.",
            result
        });
    });

    demo.MapPost("/clear", async (DemoDataSeeder seeder) =>
    {
        await seeder.ClearAsync();
        return Results.Ok(new { message = "Demo data cleared." });
    });
}

app.Run();
