using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using RunnerRunner.Server.Authentication;
using RunnerRunner.Server.Components;
using System.Security.Claims;

namespace RunnerRunner.Server.Tests.Components;

public class RedirectToLoginTests
{
    [Fact]
    public void AuthenticatedUserWithoutRunnerRunnerRoleNavigatesToPendingAccess()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings?tab=users");

        var cut = context.Render<RedirectToLogin>(parameters => parameters
            .AddCascadingValue(CreateAuthenticationState("pending-user")));

        cut.WaitForAssertion(() =>
            Assert.Equal("http://localhost/auth/pending-access?returnUrl=%2Fsettings%3Ftab%3Dusers", navigation.Uri));
    }

    [Fact]
    public void AuthenticatedUserWithRunnerRunnerRoleNavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        var cut = context.Render<RedirectToLogin>(parameters => parameters
            .AddCascadingValue(CreateAuthenticationState("viewer", RunnerRunnerRoles.Viewer)));

        cut.WaitForAssertion(() =>
            Assert.Equal("http://localhost/auth/access-denied", navigation.Uri));
    }

    [Fact]
    public void AnonymousUserNavigatesToLogin()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/jobs");

        var cut = context.Render<RedirectToLogin>(parameters => parameters
            .AddCascadingValue(CreateAnonymousAuthenticationState()));

        cut.WaitForAssertion(() =>
            Assert.Equal("http://localhost/auth/login?returnUrl=%2Fjobs", navigation.Uri));
    }

    private static Task<AuthenticationState> CreateAuthenticationState(string userName, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, userName) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, "test");

        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }

    private static Task<AuthenticationState> CreateAnonymousAuthenticationState()
        => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
}
