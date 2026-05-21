using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using RunnerRunner.Server.Authentication;

namespace RunnerRunner.Server.Tests.Authentication;

public class RunnerRunnerAuthorizationTests
{
    [Fact]
    public async Task AdministratorSatisfiesEveryManagerPolicy()
    {
        var authorization = CreateAuthorizationService();
        var user = CreatePrincipal(RunnerRunnerRoles.Administrator);

        foreach (var policy in ManagerPolicies)
        {
            var result = await authorization.AuthorizeAsync(user, policy);
            Assert.True(result.Succeeded, $"Administrator should satisfy {policy}.");
        }
    }

    [Fact]
    public async Task ManagerRoleOnlySatisfiesMatchingManagerPolicy()
    {
        var authorization = CreateAuthorizationService();
        var user = CreatePrincipal(RunnerRunnerRoles.JobsManager);

        Assert.True((await authorization.AuthorizeAsync(user, RunnerRunnerPolicies.ManageJobs)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(user, RunnerRunnerPolicies.ManageHosts)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(user, RunnerRunnerPolicies.ManageUsers)).Succeeded);
    }

    [Fact]
    public async Task ViewerCanViewButCannotManage()
    {
        var authorization = CreateAuthorizationService();
        var user = CreatePrincipal(RunnerRunnerRoles.Viewer);

        Assert.True((await authorization.AuthorizeAsync(user, RunnerRunnerPolicies.CanView)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(user, RunnerRunnerPolicies.ManageCredentials)).Succeeded);
    }

    [Fact]
    public async Task AuthenticatedUserWithoutRunnerRunnerRoleCannotView()
    {
        var authorization = CreateAuthorizationService();
        var user = CreatePrincipal();

        Assert.False((await authorization.AuthorizeAsync(user, RunnerRunnerPolicies.CanView)).Succeeded);
        Assert.False(RunnerRunnerRoles.HasAnyRole(user));
    }

    [Fact]
    public void BuiltInRolesAreStableAndUnique()
    {
        Assert.Equal(RunnerRunnerRoles.All.Length, RunnerRunnerRoles.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(RunnerRunnerRoles.Administrator, RunnerRunnerRoles.All);
        Assert.Contains(RunnerRunnerRoles.Viewer, RunnerRunnerRoles.All);
        Assert.Contains(RunnerRunnerRoles.CredentialsManager, RunnerRunnerRoles.All);
    }

    private static IAuthorizationService CreateAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => options.AddRunnerRunnerPolicies());
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal CreatePrincipal(params string[] roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static readonly string[] ManagerPolicies =
    [
        RunnerRunnerPolicies.ManageJobs,
        RunnerRunnerPolicies.ManageHosts,
        RunnerRunnerPolicies.ManageProfiles,
        RunnerRunnerPolicies.ManageCredentials,
        RunnerRunnerPolicies.ManageRegistries,
        RunnerRunnerPolicies.ManageSettings,
        RunnerRunnerPolicies.ManageUsers
    ];
}
