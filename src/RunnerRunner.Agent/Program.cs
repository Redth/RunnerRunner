using RunnerRunner.Agent;
using RunnerRunner.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SignalRConnection>();
builder.Services.AddSingleton<RunnerLifecycleManager>();
builder.Services.AddSingleton<HealthReporter>();

builder.Services.AddHostedService<AgentService>();

var host = builder.Build();
host.Run();
