using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using RunnerRunner.Server.Authentication;

namespace RunnerRunner.Server.Services.Auth;

public class RunnerRunnerOidcOptionsConfigurator(RunnerRunnerAuthSettingsService settingsService)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    private const string DisabledAuthority = "https://localhost";
    private const string DisabledClientId = "runnerrunner-disabled-oidc";

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (!string.Equals(name, RunnerRunnerAuthSchemes.Oidc, StringComparison.Ordinal))
            return;

        var settings = settingsService.Current.Oidc;

        options.SignInScheme = Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        options.CallbackPath = settings.CallbackPath;
        options.SignedOutCallbackPath = settings.SignedOutCallbackPath;
        options.Authority = DisabledAuthority;
        options.ClientId = DisabledClientId;
        options.ClientSecret = null;

        options.Scope.Clear();
        foreach (var scope in settings.Scopes)
            options.Scope.Add(scope);

        options.TokenValidationParameters.NameClaimType = settings.NameClaimType;
        options.TokenValidationParameters.RoleClaimType = settings.RoleClaimType;

        if (settings.IsConfigured)
        {
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settingsService.GetOidcClientSecret();
        }
    }
}
