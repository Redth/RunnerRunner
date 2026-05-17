using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunnerRunner.Agent.Services;
using RunnerRunner.HostWorker;
using RunnerRunner.HostWorker.Services;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "RunnerRunner HostWorker";
});

builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddSingleton(_ => HostWorkerIdentityResolver.Resolve(builder.Configuration));
builder.Services.AddSingleton<HostWorkerPaths>();
builder.Services.AddSingleton<HostWorkerLocalLogStore>();
builder.Services.AddSingleton<HostWorkerLogPublisher>();
builder.Services.AddSingleton<ILoggerProvider, HostWorkerObservedLoggerProvider>();
builder.Services.AddSingleton<HostWorkerSelfUpdater>();
builder.Services.AddSingleton<RunnerLifecycleManager>();
builder.Services.AddSingleton<HealthReporter>();
builder.Services.AddSingleton<ImageManager>();
builder.Services.AddSingleton<HostResourceUsageCollector>();

builder.Services.AddSingleton<HostCommandProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostCommandProcessor>());
builder.Services.AddSingleton<HostWorkerConnectionService>();
builder.Services.AddSingleton<IHostWorkerEventSink>(sp => sp.GetRequiredService<HostWorkerConnectionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostWorkerConnectionService>());

var host = builder.Build();
host.LogRunnerRunnerStartup();
host.Run();
