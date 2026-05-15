using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RunnerRunner.ServiceDefaults;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureRunnerRunnerLogging();

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureRunnerRunnerLogging<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.ClearProviders();
        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId;
        });

        if (ShouldUseJsonConsole(builder))
        {
            builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
                options.UseUtcTimestamp = true;
                options.JsonWriterOptions = new JsonWriterOptions
                {
                    Indented = false
                };
            });
        }
        else
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        }

        return builder;
    }

    public static IHost LogRunnerRunnerStartup(this IHost host)
    {
        var environment = host.Services.GetRequiredService<IHostEnvironment>();
        var logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("RunnerRunner.Startup");
        var buildInfo = RunnerRunnerBuildInfo.FromEntryAssembly();

        logger.LogInformation(
            "Starting {ApplicationName}. Version: {Version}; InformationalVersion: {InformationalVersion}; CommitSha: {CommitSha}; BuildTag: {BuildTag}; Configuration: {Configuration}; Environment: {EnvironmentName}; ContentRoot: {ContentRoot}; MachineName: {MachineName}; OS: {OSDescription}; Framework: {FrameworkDescription}; Containerized: {Containerized}; ProcessId: {ProcessId}",
            buildInfo.ApplicationName,
            buildInfo.AssemblyVersion,
            buildInfo.InformationalVersion,
            buildInfo.CommitSha,
            buildInfo.BuildTag,
            buildInfo.Configuration,
            environment.EnvironmentName,
            environment.ContentRootPath,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            IsRunningInContainer(),
            Environment.ProcessId);

        return host;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var buildInfo = RunnerRunnerBuildInfo.FromEntryAssembly();

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource
                    .AddService(
                        serviceName: builder.Environment.ApplicationName,
                        serviceVersion: buildInfo.InformationalVersion)
                    .AddAttributes(GetRunnerRunnerResourceAttributes(builder, buildInfo));
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static bool ShouldUseJsonConsole<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var formatterName = builder.Configuration["Logging:Console:FormatterName"];
        if (!string.IsNullOrWhiteSpace(formatterName))
            return string.Equals(formatterName, ConsoleFormatterNames.Json, StringComparison.OrdinalIgnoreCase);

        return !builder.Environment.IsDevelopment();
    }

    private static IEnumerable<KeyValuePair<string, object>> GetRunnerRunnerResourceAttributes<TBuilder>(
        TBuilder builder,
        RunnerRunnerBuildInfo buildInfo)
        where TBuilder : IHostApplicationBuilder
    {
        yield return new("deployment.environment", builder.Environment.EnvironmentName);
        yield return new("service.instance.id", Environment.MachineName);
        yield return new("runnerrunner.version.informational", buildInfo.InformationalVersion);
        yield return new("runnerrunner.build.configuration", buildInfo.Configuration);
        yield return new("runnerrunner.git.sha", buildInfo.CommitSha);
        yield return new("runnerrunner.git.tag", buildInfo.BuildTag);

        var hostId = builder.Configuration["HostWorker:HostId"];
        if (!string.IsNullOrWhiteSpace(hostId))
            yield return new("runnerrunner.host.id", hostId);

        var hostName = builder.Configuration["HostWorker:HostName"];
        if (!string.IsNullOrWhiteSpace(hostName))
            yield return new("runnerrunner.host.name", hostName);

        var platform = builder.Configuration["HostWorker:Platform"];
        if (!string.IsNullOrWhiteSpace(platform))
            yield return new("runnerrunner.host.platform", platform);
    }

    private static bool IsRunningInContainer()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
