using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RunnerRunner.Server.Authentication;
using RunnerRunner.Server.Services.Auth;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Authentication;

public class RunnerRunnerOidcOptionsConfiguratorTests
{
    [Fact]
    public void OptionsMonitor_WithDisabledSettings_CreatesOidcOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddOpenIdConnect(RunnerRunnerAuthSchemes.Oidc, _ => { });
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, RunnerRunnerOidcOptionsConfigurator>();
        services.AddSingleton(CreateAuthSettingsService());

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        var exception = Record.Exception(() => monitor.Get(RunnerRunnerAuthSchemes.Oidc));
        Assert.Null(exception);
    }

    private static RunnerRunnerAuthSettingsService CreateAuthSettingsService()
    {
        return new RunnerRunnerAuthSettingsService(
            Substitute.For<IDocumentStore>(),
            new EphemeralDataProtectionProvider(),
            new OptionsCache<OpenIdConnectOptions>(),
            NullLogger<RunnerRunnerAuthSettingsService>.Instance);
    }
}
