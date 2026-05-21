using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace RunnerRunner.Server.Authentication;

public static class RunnerRunnerRoles
{
    public const string Administrator = "Administrator";
    public const string JobsManager = "JobsManager";
    public const string HostsManager = "HostsManager";
    public const string ProfilesManager = "ProfilesManager";
    public const string CredentialsManager = "CredentialsManager";
    public const string RegistriesManager = "RegistriesManager";
    public const string SettingsManager = "SettingsManager";
    public const string Viewer = "Viewer";

    public static readonly string[] All =
    [
        Administrator,
        JobsManager,
        HostsManager,
        ProfilesManager,
        CredentialsManager,
        RegistriesManager,
        SettingsManager,
        Viewer
    ];

    public static readonly string[] ViewRoles = All;

    public static bool HasAnyRole(ClaimsPrincipal user)
        => All.Any(user.IsInRole);

    public static bool ContainsAnyRole(IEnumerable<string> roles)
        => roles.Any(role => All.Contains(role, StringComparer.Ordinal));
}

public static class RunnerRunnerPolicies
{
    public const string CanView = "RunnerRunner.CanView";
    public const string ManageJobs = "RunnerRunner.ManageJobs";
    public const string ManageHosts = "RunnerRunner.ManageHosts";
    public const string ManageProfiles = "RunnerRunner.ManageProfiles";
    public const string ManageCredentials = "RunnerRunner.ManageCredentials";
    public const string ManageRegistries = "RunnerRunner.ManageRegistries";
    public const string ManageSettings = "RunnerRunner.ManageSettings";
    public const string ManageUsers = "RunnerRunner.ManageUsers";

    public static void AddRunnerRunnerPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(CanView, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(RunnerRunnerRoles.ViewRoles));

        options.AddPolicy(ManageJobs, policy => RequireManager(policy, RunnerRunnerRoles.JobsManager));
        options.AddPolicy(ManageHosts, policy => RequireManager(policy, RunnerRunnerRoles.HostsManager));
        options.AddPolicy(ManageProfiles, policy => RequireManager(policy, RunnerRunnerRoles.ProfilesManager));
        options.AddPolicy(ManageCredentials, policy => RequireManager(policy, RunnerRunnerRoles.CredentialsManager));
        options.AddPolicy(ManageRegistries, policy => RequireManager(policy, RunnerRunnerRoles.RegistriesManager));
        options.AddPolicy(ManageSettings, policy => RequireManager(policy, RunnerRunnerRoles.SettingsManager));
        options.AddPolicy(ManageUsers, policy => RequireManager(policy));
    }

    private static void RequireManager(AuthorizationPolicyBuilder policy, params string[] roles)
    {
        var allowed = roles.Length == 0
            ? [RunnerRunnerRoles.Administrator]
            : roles.Append(RunnerRunnerRoles.Administrator).ToArray();

        policy.RequireAuthenticatedUser()
            .RequireRole(allowed);
    }
}

public static class RunnerRunnerAuthSchemes
{
    public const string Oidc = "oidc";
}
