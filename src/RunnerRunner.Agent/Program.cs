using RunnerRunner.Agent;
using RunnerRunner.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);

// Only enable Aspire service defaults when running under Aspire orchestration.
// Service discovery and resilience handlers interfere with SignalR connections
// when running standalone (deployed via launchd or Docker).
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.AddServiceDefaults();
}

builder.Services.AddSingleton<SignalRConnection>();
builder.Services.AddSingleton<RunnerLifecycleManager>();
builder.Services.AddSingleton<HealthReporter>();
builder.Services.AddSingleton<ImageManager>();
builder.Services.AddSingleton<ImagePullCoordinator>();

builder.Services.AddHostedService<AgentService>();

var host = builder.Build();
host.Run();
