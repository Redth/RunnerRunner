using RunnerRunner.Agent;
using RunnerRunner.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);

// Aspire service defaults (OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults();

builder.Services.AddSingleton<SignalRConnection>();
builder.Services.AddSingleton<RunnerLifecycleManager>();
builder.Services.AddSingleton<HealthReporter>();
builder.Services.AddSingleton<ImageManager>();

builder.Services.AddHostedService<AgentService>();

var host = builder.Build();
host.Run();
