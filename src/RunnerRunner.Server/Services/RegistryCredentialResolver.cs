using Shiny.DocumentDb;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Resolves registry credentials for Docker image pulls.
/// Matches credentials by explicit CredentialId first, then by registry URL + namespace.
/// </summary>
public static class RegistryCredentialResolver
{
    /// <summary>
    /// Resolves the best-matching registry credential for a Docker image configuration.
    /// Priority: explicit CredentialId → registry URL + repo match → registry URL + org/namespace match → default for registry.
    /// </summary>
    public static async Task<RegistryCredential?> ResolveAsync(
        IDocumentStore store,
        DockerImageConfig? dockerConfig,
        ILogger? logger = null)
    {
        if (dockerConfig == null)
            return null;

        // 1. Explicit credential binding via CredentialId
        if (!string.IsNullOrEmpty(dockerConfig.CredentialId))
        {
            var credential = await store.Get<RegistryCredential>(dockerConfig.CredentialId);
            if (credential != null)
            {
                logger?.LogDebug("Resolved registry credential by CredentialId: {Id}", dockerConfig.CredentialId);
                return credential;
            }
            logger?.LogWarning("DockerConfig references CredentialId {Id} but credential not found", dockerConfig.CredentialId);
        }

        // 2. Match by registry URL and image namespace
        var allCredentials = await store.Query<RegistryCredential>().ToList();
        if (allCredentials.Count == 0)
            return null;

        var normalizedRegistry = NormalizeRegistryUrl(dockerConfig.RegistryUrl);
        var imageName = dockerConfig.ImageName?.Trim('/') ?? "";

        // Filter to credentials matching the registry URL
        var registryMatches = allCredentials
            .Where(c => !string.IsNullOrEmpty(c.RegistryUrl) &&
                        NormalizeRegistryUrl(c.RegistryUrl).Equals(normalizedRegistry, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (registryMatches.Count == 0)
            return null;

        // 2a. Repo-level match: credential DefaultNamespace matches the owner/org of the image
        //     e.g., image "redth/ailoha/runner" matches credential with DefaultNamespace "redth/ailoha"
        var imageOrg = GetImageOrganization(imageName);
        var imageOwner = GetImageOwner(imageName);

        if (!string.IsNullOrEmpty(imageOrg))
        {
            // Try most specific match first: full org/repo path
            var repoMatch = registryMatches.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.DefaultNamespace) &&
                imageOrg.Equals(c.DefaultNamespace, StringComparison.OrdinalIgnoreCase));

            if (repoMatch != null)
            {
                logger?.LogDebug("Resolved registry credential by repo namespace match: {Name} (namespace: {Ns})",
                    repoMatch.Name, repoMatch.DefaultNamespace);
                return repoMatch;
            }
        }

        // 2b. Owner-level match: credential DefaultNamespace matches the top-level owner
        //     e.g., image "redth/ailoha/runner" matches credential with DefaultNamespace "redth"
        if (!string.IsNullOrEmpty(imageOwner))
        {
            var ownerMatch = registryMatches.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.DefaultNamespace) &&
                imageOwner.Equals(c.DefaultNamespace, StringComparison.OrdinalIgnoreCase));

            if (ownerMatch != null)
            {
                logger?.LogDebug("Resolved registry credential by owner namespace match: {Name} (namespace: {Ns})",
                    ownerMatch.Name, ownerMatch.DefaultNamespace);
                return ownerMatch;
            }
        }

        // 2c. Default credential for this registry
        var defaultMatch = registryMatches.FirstOrDefault(c => c.IsDefault);
        if (defaultMatch != null)
        {
            logger?.LogDebug("Resolved registry credential by default flag: {Name}", defaultMatch.Name);
            return defaultMatch;
        }

        // 2d. Any credential for this registry (last resort)
        var anyMatch = registryMatches.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.Username) && !string.IsNullOrEmpty(c.Password));
        if (anyMatch != null)
        {
            logger?.LogDebug("Resolved registry credential by registry URL fallback: {Name}", anyMatch.Name);
            return anyMatch;
        }

        return null;
    }

    /// <summary>
    /// Extracts the full org path (all segments except the last) from an image name.
    /// e.g., "redth/ailoha/runner" → "redth/ailoha", "redth/runner" → "redth"
    /// </summary>
    private static string? GetImageOrganization(string imageName)
    {
        var lastSlash = imageName.LastIndexOf('/');
        return lastSlash > 0 ? imageName[..lastSlash] : null;
    }

    /// <summary>
    /// Extracts the top-level owner from an image name.
    /// e.g., "redth/ailoha/runner" → "redth", "redth/runner" → "redth"
    /// </summary>
    private static string? GetImageOwner(string imageName)
    {
        var firstSlash = imageName.IndexOf('/');
        return firstSlash > 0 ? imageName[..firstSlash] : null;
    }

    private static string NormalizeRegistryUrl(string? registryUrl)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
            return string.Empty;

        var trimmed = registryUrl.Trim().TrimEnd('/');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Authority))
            return absolute.Authority;

        return trimmed
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
    }
}
