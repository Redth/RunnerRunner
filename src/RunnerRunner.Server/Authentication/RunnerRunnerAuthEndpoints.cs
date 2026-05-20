using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RunnerRunner.Server.Data.Auth;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.Auth;

namespace RunnerRunner.Server.Authentication;

public static class RunnerRunnerAuthEndpoints
{
    public static IEndpointRouteBuilder MapRunnerRunnerAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth");

        auth.MapPost("/login", LoginAsync).AllowAnonymous();
        auth.MapPost("/logout", LogoutAsync);
        auth.MapPost("/setup", SetupAsync).AllowAnonymous();
        auth.MapGet("/oidc", StartOidcAsync).AllowAnonymous();
        auth.MapGet("/oidc-complete", CompleteOidcAsync).AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        UserManager<RunnerRunnerUser> userManager,
        SignInManager<RunnerRunnerUser> signInManager,
        AuditService auditService)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        var form = await httpContext.Request.ReadFormAsync();
        var identifier = form["identifier"].ToString().Trim();
        var password = form["password"].ToString();
        var rememberMe = string.Equals(form["rememberMe"].ToString(), "on", StringComparison.OrdinalIgnoreCase);
        var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString());

        var user = await FindByUserNameOrEmailAsync(userManager, identifier);
        if (user is null || !user.Enabled)
        {
            await auditService.LogAsync("LoginFailed", "User", details: $"identifier={identifier}; reason=invalid-or-disabled");
            return RedirectToLogin(returnUrl, "invalid");
        }

        var result = await signInManager.PasswordSignInAsync(user.UserName!, password, rememberMe, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            await auditService.LogAsync("LoginFailed", "User", user.Id, $"user={user.UserName}; reason={(result.IsLockedOut ? "locked" : "invalid")}");
            return RedirectToLogin(returnUrl, result.IsLockedOut ? "locked" : "invalid");
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
        await auditService.LogAsync("LoginSucceeded", "User", user.Id, $"user={user.UserName}");

        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        SignInManager<RunnerRunnerUser> signInManager,
        AuditService auditService)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        await auditService.LogAsync("Logout", "User", httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), $"user={httpContext.User.Identity?.Name}");
        await signInManager.SignOutAsync();
        return Results.LocalRedirect("/auth/login?loggedOut=true");
    }

    private static async Task<IResult> SetupAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        FirstRunSetupService setupService,
        SignInManager<RunnerRunnerUser> signInManager,
        UserManager<RunnerRunnerUser> userManager,
        AuditService auditService)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        var form = await httpContext.Request.ReadFormAsync();
        var userName = form["userName"].ToString().Trim();
        var email = form["email"].ToString().Trim();
        var displayName = form["displayName"].ToString().Trim();
        var password = form["password"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();

        if (password != confirmPassword)
            return Results.LocalRedirect("/setup?error=password-mismatch");

        var result = await setupService.CreateInitialAdministratorAsync(userName, email, displayName, password);
        if (!result.Succeeded)
            return Results.LocalRedirect($"/setup?error={Uri.EscapeDataString(result.Errors.First().Description)}");

        var user = await userManager.FindByNameAsync(userName);
        if (user is not null)
        {
            await signInManager.SignInAsync(user, isPersistent: false);
            await auditService.LogAsync("InitialAdministratorCreated", "User", user.Id, $"user={user.UserName}");
        }

        return Results.LocalRedirect("/");
    }

    private static async Task<IResult> StartOidcAsync(
        string? returnUrl,
        RunnerRunnerAuthSettingsService settingsService,
        SignInManager<RunnerRunnerUser> signInManager)
    {
        var settings = await settingsService.GetAsync();
        if (!settings.Oidc.IsConfigured)
            return Results.LocalRedirect($"/auth/login?returnUrl={Uri.EscapeDataString(SanitizeReturnUrl(returnUrl))}&error=oidc-disabled");

        var redirectUrl = $"/auth/oidc-complete?returnUrl={Uri.EscapeDataString(SanitizeReturnUrl(returnUrl))}";
        var properties = signInManager.ConfigureExternalAuthenticationProperties(RunnerRunnerAuthSchemes.Oidc, redirectUrl);
        return Results.Challenge(properties, [RunnerRunnerAuthSchemes.Oidc]);
    }

    private static async Task<IResult> CompleteOidcAsync(
        string? returnUrl,
        RunnerRunnerAuthSettingsService settingsService,
        UserManager<RunnerRunnerUser> userManager,
        SignInManager<RunnerRunnerUser> signInManager,
        AuditService auditService)
    {
        var loginInfo = await signInManager.GetExternalLoginInfoAsync();
        if (loginInfo is null)
        {
            await auditService.LogAsync("OidcLoginFailed", "User", details: "External login info was not available.");
            return Results.LocalRedirect($"/auth/login?returnUrl={Uri.EscapeDataString(SanitizeReturnUrl(returnUrl))}&error=oidc-failed");
        }

        var user = await userManager.FindByLoginAsync(loginInfo.LoginProvider, loginInfo.ProviderKey);
        var createdUser = false;
        if (user is null)
        {
            var settings = await settingsService.GetAsync();
            user = CreateOidcUser(loginInfo);
            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                await auditService.LogAsync("OidcUserCreateFailed", "User", details: createResult.Errors.First().Description);
                return Results.LocalRedirect($"/auth/login?error={Uri.EscapeDataString(createResult.Errors.First().Description)}");
            }

            var loginResult = await userManager.AddLoginAsync(user, loginInfo);
            if (!loginResult.Succeeded)
            {
                await auditService.LogAsync("OidcUserLinkFailed", "User", user.Id, loginResult.Errors.First().Description);
                return Results.LocalRedirect($"/auth/login?error={Uri.EscapeDataString(loginResult.Errors.First().Description)}");
            }

            if (RunnerRunnerRoles.All.Contains(settings.Oidc.DefaultRole, StringComparer.Ordinal))
                await userManager.AddToRoleAsync(user, settings.Oidc.DefaultRole);

            createdUser = true;
        }

        if (!user.Enabled)
        {
            await auditService.LogAsync("OidcLoginDenied", "User", user.Id, $"user={user.UserName}; reason=disabled");
            return Results.LocalRedirect("/auth/access-denied");
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
        await signInManager.SignInAsync(user, isPersistent: false, loginInfo.LoginProvider);
        await httpContextSafeExternalCookieSignOut(signInManager);
        await auditService.LogAsync(createdUser ? "OidcUserCreatedAndLoggedIn" : "OidcLoginSucceeded", "User", user.Id, $"user={user.UserName}; provider={loginInfo.LoginProvider}");

        return Results.LocalRedirect(SanitizeReturnUrl(returnUrl));

        static Task httpContextSafeExternalCookieSignOut(SignInManager<RunnerRunnerUser> signInManager)
            => signInManager.Context.SignOutAsync(IdentityConstants.ExternalScheme);
    }

    private static RunnerRunnerUser CreateOidcUser(ExternalLoginInfo loginInfo)
    {
        var principal = loginInfo.Principal;
        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? "";
        var name = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("name")
            ?? email
            ?? loginInfo.ProviderKey;
        var issuer = principal.FindFirstValue("iss")
            ?? principal.Identity?.AuthenticationType
            ?? loginInfo.LoginProvider;
        var userNameBase = !string.IsNullOrWhiteSpace(email) ? email : $"{loginInfo.LoginProvider}-{loginInfo.ProviderKey}";

        return new RunnerRunnerUser
        {
            UserName = userNameBase,
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            EmailConfirmed = !string.IsNullOrWhiteSpace(email),
            DisplayName = name,
            Source = RunnerRunnerUserSources.Oidc,
            Enabled = true,
            ExternalIssuer = issuer,
            ExternalSubject = loginInfo.ProviderKey
        };
    }

    private static async Task<RunnerRunnerUser?> FindByUserNameOrEmailAsync(
        UserManager<RunnerRunnerUser> userManager,
        string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        var user = await userManager.FindByNameAsync(identifier);
        if (user is not null)
            return user;

        return await userManager.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == identifier.ToUpperInvariant());
    }

    private static IResult RedirectToLogin(string returnUrl, string error)
        => Results.LocalRedirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error={Uri.EscapeDataString(error)}");

    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
            return "/";

        return returnUrl;
    }
}
