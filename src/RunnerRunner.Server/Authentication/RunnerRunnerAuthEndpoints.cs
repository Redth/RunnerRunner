using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RunnerRunner.Server.Data.Auth;
using RunnerRunner.Server.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.Auth;

namespace RunnerRunner.Server.Authentication;

public static class RunnerRunnerAuthEndpoints
{
    public static IEndpointRouteBuilder MapRunnerRunnerAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth");

        auth.MapPost("/login/password", LoginAsync).AllowAnonymous();
        auth.MapPost("/logout", LogoutAsync);
        auth.MapPost("/setup", SetupAsync).AllowAnonymous();
        auth.MapGet("/oidc", StartOidcAsync).AllowAnonymous();
        auth.MapGet("/oidc-complete", CompleteOidcAsync).AllowAnonymous();
        auth.MapPost("/refresh-access", RefreshAccessAsync);

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

        var settings = await settingsService.GetAsync();
        var resolution = await GetOrCreateOidcUserAsync(userManager, loginInfo, settings.Oidc);
        if (!resolution.Succeeded || resolution.User is null)
        {
            var error = resolution.Result.Errors.FirstOrDefault()?.Description ?? "Unable to complete OIDC sign-in.";
            await auditService.LogAsync(resolution.FailedAuditAction, "User", resolution.User?.Id, error);
            return Results.LocalRedirect($"/auth/login?error={Uri.EscapeDataString(error)}");
        }

        var user = resolution.User;
        if (resolution.CreatedUser && RunnerRunnerRoles.All.Contains(settings.Oidc.DefaultRole, StringComparer.Ordinal))
        {
            var roleResult = await userManager.AddToRoleAsync(user, settings.Oidc.DefaultRole);
            if (!roleResult.Succeeded)
            {
                await auditService.LogAsync("OidcUserRoleAddFailed", "User", user.Id, roleResult.Errors.First().Description);
                return Results.LocalRedirect($"/auth/login?error={Uri.EscapeDataString(roleResult.Errors.First().Description)}");
            }
        }

        if (!user.Enabled)
        {
            await auditService.LogAsync("OidcLoginDenied", "User", user.Id, $"user={user.UserName}; reason=disabled");
            return Results.LocalRedirect("/auth/access-denied");
        }

        var roles = await userManager.GetRolesAsync(user);
        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
        await signInManager.SignInAsync(user, isPersistent: false, loginInfo.LoginProvider);
        await httpContextSafeExternalCookieSignOut(signInManager);
        await auditService.LogAsync(GetSuccessfulOidcAuditAction(resolution), "User", user.Id, $"user={user.UserName}; provider={loginInfo.LoginProvider}");

        var sanitizedReturnUrl = SanitizeReturnUrl(returnUrl);
        return RunnerRunnerRoles.ContainsAnyRole(roles)
            ? Results.LocalRedirect(sanitizedReturnUrl)
            : RedirectToPendingAccess(sanitizedReturnUrl);

        static Task httpContextSafeExternalCookieSignOut(SignInManager<RunnerRunnerUser> signInManager)
            => signInManager.Context.SignOutAsync(IdentityConstants.ExternalScheme);
    }

    private static async Task<IResult> RefreshAccessAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        UserManager<RunnerRunnerUser> userManager,
        SignInManager<RunnerRunnerUser> signInManager,
        AuditService auditService)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        var form = await httpContext.Request.ReadFormAsync();
        var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString());

        if (httpContext.User.Identity?.IsAuthenticated != true)
            return Results.LocalRedirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
        {
            await signInManager.SignOutAsync();
            return Results.LocalRedirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        if (!user.Enabled)
        {
            await auditService.LogAsync("AccessRefreshDenied", "User", user.Id, $"user={user.UserName}; reason=disabled");
            await signInManager.SignOutAsync();
            return Results.LocalRedirect("/auth/access-denied");
        }

        await signInManager.RefreshSignInAsync(user);
        var roles = await userManager.GetRolesAsync(user);

        return RunnerRunnerRoles.ContainsAnyRole(roles)
            ? Results.LocalRedirect(returnUrl)
            : RedirectToPendingAccess(returnUrl, accessChecked: true);
    }

    internal static async Task<OidcUserResolution> GetOrCreateOidcUserAsync(
        UserManager<RunnerRunnerUser> userManager,
        ExternalLoginInfo loginInfo,
        RunnerRunnerOidcSettings oidcSettings)
    {
        var user = await userManager.FindByLoginAsync(loginInfo.LoginProvider, loginInfo.ProviderKey);
        if (user is not null)
            return OidcUserResolution.Existing(user);

        var profile = CreateOidcUserProfile(loginInfo, oidcSettings);
        if (!string.IsNullOrWhiteSpace(profile.Email))
        {
            user = await userManager.FindByEmailAsync(profile.Email);
            if (user is not null)
            {
                ApplyOidcMetadata(user, profile);
                var loginResult = await userManager.AddLoginAsync(user, loginInfo);
                return loginResult.Succeeded
                    ? OidcUserResolution.Linked(user)
                    : OidcUserResolution.Failed(user, loginResult, "OidcUserLinkFailed");
            }
        }

        user = CreateOidcUser(profile);
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            return OidcUserResolution.Failed(user, createResult, "OidcUserCreateFailed");

        var addLoginResult = await userManager.AddLoginAsync(user, loginInfo);
        if (!addLoginResult.Succeeded)
            return OidcUserResolution.Failed(user, addLoginResult, "OidcUserLinkFailed");

        return OidcUserResolution.Created(user);
    }

    private static RunnerRunnerUser CreateOidcUser(OidcUserProfile profile)
    {
        return new RunnerRunnerUser
        {
            UserName = profile.UserNameBase,
            Email = profile.Email,
            EmailConfirmed = !string.IsNullOrWhiteSpace(profile.Email),
            DisplayName = profile.Name,
            Source = RunnerRunnerUserSources.Oidc,
            Enabled = true,
            ExternalIssuer = profile.Issuer,
            ExternalSubject = profile.Subject
        };
    }

    private static OidcUserProfile CreateOidcUserProfile(
        ExternalLoginInfo loginInfo,
        RunnerRunnerOidcSettings oidcSettings)
    {
        var principal = loginInfo.Principal;
        var email = FindFirstClaimValue(principal, oidcSettings.EmailClaimType, ClaimTypes.Email, "email");
        var name = FindFirstClaimValue(principal, oidcSettings.NameClaimType, ClaimTypes.Name, "name")
            ?? email
            ?? loginInfo.ProviderKey;
        var issuer = FindFirstClaimValue(principal, "iss")
            ?? principal.Identity?.AuthenticationType
            ?? loginInfo.LoginProvider;
        var userNameBase = !string.IsNullOrWhiteSpace(email) ? email : $"{loginInfo.LoginProvider}-{loginInfo.ProviderKey}";

        return new OidcUserProfile(
            UserNameBase: userNameBase,
            Email: string.IsNullOrWhiteSpace(email) ? null : email,
            Name: name,
            Issuer: issuer,
            Subject: loginInfo.ProviderKey);
    }

    private static void ApplyOidcMetadata(RunnerRunnerUser user, OidcUserProfile profile)
    {
        if (string.IsNullOrWhiteSpace(user.DisplayName))
            user.DisplayName = profile.Name;

        user.ExternalIssuer = profile.Issuer;
        user.ExternalSubject = profile.Subject;
        user.UpdatedAt = DateTime.UtcNow;
    }

    private static string? FindFirstClaimValue(ClaimsPrincipal principal, params string?[] claimTypes)
        => claimTypes
            .Where(claimType => !string.IsNullOrWhiteSpace(claimType))
            .Select(claimType => principal.FindFirstValue(claimType!))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

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

    private static string GetSuccessfulOidcAuditAction(OidcUserResolution resolution)
    {
        if (resolution.CreatedUser)
            return "OidcUserCreatedAndLoggedIn";

        return resolution.LinkedExistingUser ? "OidcUserLinkedAndLoggedIn" : "OidcLoginSucceeded";
    }

    private static IResult RedirectToPendingAccess(string returnUrl, bool accessChecked = false)
    {
        var target = $"/auth/pending-access?returnUrl={Uri.EscapeDataString(returnUrl)}";
        if (accessChecked)
            target += "&checked=true";

        return Results.LocalRedirect(target);
    }

    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
            return "/";

        return returnUrl;
    }

    internal sealed record OidcUserResolution(
        RunnerRunnerUser? User,
        IdentityResult Result,
        bool CreatedUser,
        bool LinkedExistingUser,
        string FailedAuditAction)
    {
        public bool Succeeded => Result.Succeeded && User is not null;

        public static OidcUserResolution Existing(RunnerRunnerUser user)
            => new(user, IdentityResult.Success, CreatedUser: false, LinkedExistingUser: false, FailedAuditAction: "OidcLoginFailed");

        public static OidcUserResolution Linked(RunnerRunnerUser user)
            => new(user, IdentityResult.Success, CreatedUser: false, LinkedExistingUser: true, FailedAuditAction: "OidcUserLinkFailed");

        public static OidcUserResolution Created(RunnerRunnerUser user)
            => new(user, IdentityResult.Success, CreatedUser: true, LinkedExistingUser: false, FailedAuditAction: "OidcUserCreateFailed");

        public static OidcUserResolution Failed(RunnerRunnerUser? user, IdentityResult result, string failedAuditAction)
            => new(user, result, CreatedUser: false, LinkedExistingUser: false, failedAuditAction);
    }

    private sealed record OidcUserProfile(
        string UserNameBase,
        string? Email,
        string Name,
        string Issuer,
        string Subject);
}
