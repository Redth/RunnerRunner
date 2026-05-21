using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RunnerRunner.Server.Authentication;
using RunnerRunner.Server.Data.Auth;
using RunnerRunner.Server.Models;

namespace RunnerRunner.Server.Tests.Authentication;

public class RunnerRunnerOidcUserResolutionTests
{
    [Fact]
    public async Task GetOrCreateOidcUserAsyncLinksExistingLocalUserWithMatchingEmail()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var userManager = fixture.Provider.GetRequiredService<UserManager<RunnerRunnerUser>>();
        var localUser = new RunnerRunnerUser
        {
            UserName = "direct-user",
            Email = "person@example.com",
            EmailConfirmed = true,
            DisplayName = "Direct User",
            Source = RunnerRunnerUserSources.Local,
            Enabled = true
        };
        AssertIdentitySuccess(await userManager.CreateAsync(localUser));

        var loginInfo = CreateLoginInfo("pocket-subject", ("email", "person@example.com"));

        var result = await RunnerRunnerAuthEndpoints.GetOrCreateOidcUserAsync(
            userManager,
            loginInfo,
            new RunnerRunnerOidcSettings());

        Assert.True(result.Succeeded);
        Assert.False(result.CreatedUser);
        Assert.True(result.LinkedExistingUser);
        Assert.Equal(localUser.Id, result.User!.Id);
        Assert.Equal(RunnerRunnerUserSources.Local, result.User.Source);
        Assert.Equal("pocket-subject", result.User.ExternalSubject);
        Assert.Equal(1, await userManager.Users.CountAsync());

        var linkedLogins = await userManager.GetLoginsAsync(result.User);
        Assert.Contains(linkedLogins, login =>
            login.LoginProvider == RunnerRunnerAuthSchemes.Oidc &&
            login.ProviderKey == "pocket-subject");
    }

    [Fact]
    public async Task GetOrCreateOidcUserAsyncCreatesOidcUserWhenEmailDoesNotExist()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var userManager = fixture.Provider.GetRequiredService<UserManager<RunnerRunnerUser>>();
        var loginInfo = CreateLoginInfo("new-pocket-subject", ("email", "new-person@example.com"));

        var result = await RunnerRunnerAuthEndpoints.GetOrCreateOidcUserAsync(
            userManager,
            loginInfo,
            new RunnerRunnerOidcSettings());

        Assert.True(result.Succeeded);
        Assert.True(result.CreatedUser);
        Assert.False(result.LinkedExistingUser);
        Assert.Equal("new-person@example.com", result.User!.Email);
        Assert.Equal("new-person@example.com", result.User.UserName);
        Assert.Equal(RunnerRunnerUserSources.Oidc, result.User.Source);

        var linkedLogins = await userManager.GetLoginsAsync(result.User);
        Assert.Contains(linkedLogins, login =>
            login.LoginProvider == RunnerRunnerAuthSchemes.Oidc &&
            login.ProviderKey == "new-pocket-subject");
    }

    [Fact]
    public async Task GetOrCreateOidcUserAsyncUsesConfiguredEmailClaimForLinking()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var userManager = fixture.Provider.GetRequiredService<UserManager<RunnerRunnerUser>>();
        var localUser = new RunnerRunnerUser
        {
            UserName = "custom-claim-user",
            Email = "custom@example.com",
            EmailConfirmed = true,
            DisplayName = "Custom Claim User",
            Source = RunnerRunnerUserSources.Local,
            Enabled = true
        };
        AssertIdentitySuccess(await userManager.CreateAsync(localUser));

        var loginInfo = CreateLoginInfo("custom-pocket-subject", ("mail", "custom@example.com"));

        var result = await RunnerRunnerAuthEndpoints.GetOrCreateOidcUserAsync(
            userManager,
            loginInfo,
            new RunnerRunnerOidcSettings { EmailClaimType = "mail" });

        Assert.True(result.Succeeded);
        Assert.True(result.LinkedExistingUser);
        Assert.Equal(localUser.Id, result.User!.Id);
        Assert.Equal(1, await userManager.Users.CountAsync());
    }

    private static ExternalLoginInfo CreateLoginInfo(string subject, params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(RunnerRunnerAuthSchemes.Oidc);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        identity.AddClaim(new Claim(ClaimTypes.Name, "Pocket ID User"));
        identity.AddClaim(new Claim("iss", "https://pocketid.example.test"));
        foreach (var (type, value) in claims)
            identity.AddClaim(new Claim(type, value));

        return new ExternalLoginInfo(
            new ClaimsPrincipal(identity),
            RunnerRunnerAuthSchemes.Oidc,
            subject,
            "Pocket ID");
    }

    private static void AssertIdentitySuccess(IdentityResult result)
    {
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors.Select(error => error.Description)));
    }

    private sealed class IdentityFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private IdentityFixture(SqliteConnection connection, ServiceProvider provider)
        {
            _connection = connection;
            Provider = provider;
        }

        public ServiceProvider Provider { get; }

        public static async Task<IdentityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<RunnerRunnerAuthDbContext>(options => options.UseSqlite(connection));
            services
                .AddIdentity<RunnerRunnerUser, IdentityRole>(options => options.User.RequireUniqueEmail = true)
                .AddEntityFrameworkStores<RunnerRunnerAuthDbContext>()
                .AddDefaultTokenProviders();

            var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RunnerRunnerAuthDbContext>();
            await db.Database.EnsureCreatedAsync();

            return new IdentityFixture(connection, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
