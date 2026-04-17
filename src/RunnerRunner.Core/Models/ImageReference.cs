namespace RunnerRunner.Core.Models;

public static class ImageReference
{
    public static string Build(string? registryUrl, string imageName, string? tag = null)
    {
        var (repository, existingTag) = Normalize(registryUrl, imageName);
        var resolvedTag = string.IsNullOrWhiteSpace(existingTag) ? tag : existingTag;

        return string.IsNullOrWhiteSpace(resolvedTag)
            ? repository
            : $"{repository}:{resolvedTag}";
    }

    public static string BuildRepository(string? registryUrl, string imageName) =>
        Normalize(registryUrl, imageName).Repository;

    private static (string Repository, string? Tag) Normalize(string? registryUrl, string imageName)
    {
        var registry = NormalizeRegistry(registryUrl);
        var normalizedImage = (imageName ?? string.Empty).Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedImage))
            return (string.Empty, null);

        var repository = normalizedImage;
        string? existingTag = null;

        var lastSlash = repository.LastIndexOf('/');
        var lastColon = repository.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            existingTag = repository[(lastColon + 1)..];
            repository = repository[..lastColon];
        }

        if (!string.IsNullOrWhiteSpace(registry)
            && !repository.Equals(registry, StringComparison.OrdinalIgnoreCase)
            && !repository.StartsWith($"{registry}/", StringComparison.OrdinalIgnoreCase))
        {
            repository = $"{registry}/{repository}";
        }

        return (repository, existingTag);
    }

    private static string NormalizeRegistry(string? registryUrl)
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
