using Shiny.DocumentDb;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Background service that periodically checks for new runner agent versions
/// from all configured providers and stores them in the document store.
/// </summary>
public class VersionCheckService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<VersionCheckService> _logger;

    public VersionCheckService(IServiceProvider services, ILogger<VersionCheckService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Version check service started");

        // Initial check after a short delay
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckVersionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Version check failed");
            }

            // Check every 6 hours
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task CheckVersionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var providers = scope.ServiceProvider.GetServices<IRunnerProviderPlugin>();

        foreach (var provider in providers)
        {
            try
            {
                var versions = await provider.GetAvailableVersionsAsync(ct);
                var allExisting = (await store.Query<RunnerAgentVersion>().ToList()).ToList();
                var existing = allExisting.Where(v => v.Provider == provider.Provider).ToList();

                var existingVersionStrings = existing.Select(v => v.Version).ToHashSet();

                // Reset IsLatest on all existing versions for this provider
                foreach (var ev in existing.Where(v => v.IsLatest))
                {
                    ev.IsLatest = false;
                    await store.Update(ev);
                }

                foreach (var version in versions)
                {
                    if (existingVersionStrings.Contains(version.Version))
                    {
                        // Update IsLatest flag
                        var existingVersion = existing.First(v => v.Version == version.Version);
                        existingVersion.IsLatest = version.IsLatest;
                        await store.Update(existingVersion);
                    }
                    else
                    {
                        await store.Insert(version);
                        _logger.LogInformation("New {Provider} runner version discovered: {Version}",
                            provider.Provider, version.Version);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check versions for {Provider}", provider.Provider);
            }
        }
    }
}
