using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Shiny.DocumentDb;
using RunnerRunner.Server.Authentication;
using RunnerRunner.Server.Models;

namespace RunnerRunner.Server.Services.Auth;

public class RunnerRunnerAuthSettingsService
{
    private readonly IDocumentStore _store;
    private readonly IDataProtector _protector;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcOptionsCache;
    private readonly ILogger<RunnerRunnerAuthSettingsService> _logger;
    private RunnerRunnerAuthSettings _current = new();

    public RunnerRunnerAuthSettingsService(
        IDocumentStore store,
        IDataProtectionProvider dataProtection,
        IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache,
        ILogger<RunnerRunnerAuthSettingsService> logger)
    {
        _store = store;
        _protector = dataProtection.CreateProtector("RunnerRunner.AuthSettings.ClientSecrets");
        _oidcOptionsCache = oidcOptionsCache;
        _logger = logger;
    }

    public RunnerRunnerAuthSettings Current => CloneSettings(_current);

    public async Task LoadAsync()
    {
        _current = await ReadStoredAsync();
    }

    public async Task<RunnerRunnerAuthSettings> GetAsync()
    {
        if (_current.Id != RunnerRunnerAuthSettings.SingletonId)
            await LoadAsync();

        return Current;
    }

    public async Task SaveAsync(RunnerRunnerAuthSettings settings, string? clientSecret)
    {
        settings.Id = RunnerRunnerAuthSettings.SingletonId;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.Oidc.Authority = settings.Oidc.Authority.Trim().TrimEnd('/');
        settings.Oidc.ClientId = settings.Oidc.ClientId.Trim();
        settings.Oidc.CallbackPath = NormalizePath(settings.Oidc.CallbackPath, "/signin-oidc");
        settings.Oidc.SignedOutCallbackPath = NormalizePath(settings.Oidc.SignedOutCallbackPath, "/signout-callback-oidc");
        settings.Oidc.Scopes = NormalizeScopes(settings.Oidc.Scopes);
        settings.Oidc.NameClaimType = NormalizeClaim(settings.Oidc.NameClaimType, "name");
        settings.Oidc.EmailClaimType = NormalizeClaim(settings.Oidc.EmailClaimType, "email");
        settings.Oidc.RoleClaimType = NormalizeClaim(settings.Oidc.RoleClaimType, "role");

        if (!string.IsNullOrWhiteSpace(clientSecret))
            settings.Oidc.ProtectedClientSecret = _protector.Protect(clientSecret);
        else if (string.IsNullOrWhiteSpace(settings.Oidc.ProtectedClientSecret))
            settings.Oidc.ProtectedClientSecret = _current.Oidc.ProtectedClientSecret;

        var existing = await TryGetStoredAsync();
        if (existing is null)
            await _store.Insert(settings);
        else
            await _store.Update(settings);

        _current = CloneSettings(settings);
        _oidcOptionsCache.TryRemove(RunnerRunnerAuthSchemes.Oidc);
        _logger.LogInformation("OIDC authentication settings were updated.");
    }

    public string? GetOidcClientSecret()
    {
        if (string.IsNullOrWhiteSpace(_current.Oidc.ProtectedClientSecret))
            return null;

        try
        {
            return _protector.Unprotect(_current.Oidc.ProtectedClientSecret);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "OIDC client secret could not be decrypted. Re-save OIDC settings to restore single sign-on.");
            return null;
        }
    }

    private async Task<RunnerRunnerAuthSettings> ReadStoredAsync()
    {
        return await TryGetStoredAsync() ?? new RunnerRunnerAuthSettings();
    }

    private async Task<RunnerRunnerAuthSettings?> TryGetStoredAsync()
    {
        try
        {
            return await _store.Get<RunnerRunnerAuthSettings>(RunnerRunnerAuthSettings.SingletonId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auth settings were not found; defaults will be used.");
            return null;
        }
    }

    private static string NormalizePath(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var trimmed = value.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string NormalizeClaim(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static List<string> NormalizeScopes(IEnumerable<string>? scopes)
    {
        var normalized = (scopes ?? [])
            .SelectMany(x => x.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var required in new[] { "openid", "profile", "email" })
        {
            if (!normalized.Contains(required, StringComparer.Ordinal))
                normalized.Insert(0, required);
        }

        return normalized;
    }

    private static RunnerRunnerAuthSettings CloneSettings(RunnerRunnerAuthSettings settings)
    {
        return new RunnerRunnerAuthSettings
        {
            Id = settings.Id,
            UpdatedAt = settings.UpdatedAt,
            Oidc = new RunnerRunnerOidcSettings
            {
                Enabled = settings.Oidc.Enabled,
                DisplayName = settings.Oidc.DisplayName,
                Authority = settings.Oidc.Authority,
                ClientId = settings.Oidc.ClientId,
                ProtectedClientSecret = settings.Oidc.ProtectedClientSecret,
                CallbackPath = settings.Oidc.CallbackPath,
                SignedOutCallbackPath = settings.Oidc.SignedOutCallbackPath,
                Scopes = [.. settings.Oidc.Scopes],
                NameClaimType = settings.Oidc.NameClaimType,
                EmailClaimType = settings.Oidc.EmailClaimType,
                RoleClaimType = settings.Oidc.RoleClaimType,
                DefaultRole = settings.Oidc.DefaultRole,
                RequireHttpsMetadata = settings.Oidc.RequireHttpsMetadata
            }
        };
    }
}
